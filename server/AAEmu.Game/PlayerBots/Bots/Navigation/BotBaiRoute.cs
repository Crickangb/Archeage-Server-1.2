using System.Numerics;
using AAEmu.Game.Models.CryEngine.Entities;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Bots.Navigation;

/// <summary>Local routes retain native graph edges, including raised bridge decks.</summary>
internal static class BotBaiRoute
{
    internal static IReadOnlyList<Vector3> Search(NodeDescriptor start, NodeDescriptor goal, int budget=8192)
    {
        if(start==null||goal==null||!ReferenceEquals(start.NetMission,goal.NetMission))return [];
        var edges=start.NetMission.LinkDescriptorList.Where(Passable).GroupBy(l=>l.SourceNode)
            .ToDictionary(g=>(long)g.Key,g=>g.ToArray());
        var frontier=new PriorityQueue<NodeDescriptor,float>();
        var cost=new Dictionary<long,float>{{start.Id,0}};
        var previous=new Dictionary<long,NodeDescriptor>();
        var closed=new HashSet<long>();
        frontier.Enqueue(start,Vector3.Distance(start.Pos,goal.Pos));
        while(frontier.TryDequeue(out var node,out _)&&budget-->0)
        {
            if(!closed.Add(node.Id))continue;
            if(node.Id==goal.Id)
            {
                var path=new List<Vector3>{node.Pos};
                while(previous.TryGetValue(node.Id,out var parent)){node=parent;path.Add(node.Pos);}
                path.Reverse();return path;
            }
            if(!edges.TryGetValue(node.Id,out var links))continue;
            foreach(var link in links)
            {
                var next=link.TargetNodeDescriptor;
                var nextCost=cost[node.Id]+Vector3.Distance(node.Pos,next.Pos);
                if(closed.Contains(next.Id)||cost.TryGetValue(next.Id,out var old)&&old<=nextCost)continue;
                cost[next.Id]=nextCost;previous[next.Id]=node;
                frontier.Enqueue(next,nextCost+Vector3.Distance(next.Pos,goal.Pos));
            }
        }
        // Exhaustion is failure, never an invented straight connection to the goal.
        return [];
    }

    private static bool Passable(LinkDescriptor link)
    {
        var a=link.SourceNodeDescriptor;var b=link.TargetNodeDescriptor;
        if(a==null||b==null||a.Type is not (2 or 4)||b.Type is not (2 or 4)||
           !float.IsFinite(a.Pos.X+a.Pos.Y+a.Pos.Z+b.Pos.X+b.Pos.Y+b.Pos.Z)||
           !double.IsFinite(link.MaxPassRadius)||link.MaxPassRadius<=0||
           !double.IsFinite(link.MaxWaterDepth)||link.MaxWaterDepth>.4)return false;
        var horizontal=new Vector2(a.Pos.X-b.Pos.X,a.Pos.Y-b.Pos.Y).Length();
        return horizontal>.01f&&Math.Abs(a.Pos.Z-b.Pos.Z)<=Math.Max(.5f,horizontal*.7f);
    }

    internal static bool HasNearbyData(Character bot)
    {
        var point=bot.ParentWorld?.Template?.GeoData?.FindСlosestToTheCurrent(bot.Transform.ZoneId,bot.Transform.World.Position);
        return point!=null&&Vector3.Distance(point.Pos,bot.Transform.World.Position)<=15f;
    }

    internal static BotTravelRoute Plan(Character bot, Vector3 destination, bool allowPartial=false, Action checkBudget=null)
        => Plan(bot, destination, out _, allowPartial, checkBudget);

    internal static BotTravelRoute Plan(Character bot, Vector3 destination, out string failure, bool allowPartial=false, Action checkBudget=null)
    {
        checkBudget?.Invoke();
        failure = null;
        var world=bot.ParentWorld;var geo=world?.Template?.GeoData;
        if(geo==null) { failure = "geodata_unavailable"; return null; }
        var origin=bot.Transform.World.Position;
        var start=geo.FindСlosestToTheCurrent(bot.Transform.ZoneId,origin);
        if(start==null) { failure = "start_node_missing"; return null; }
        bool Water(Vector3 point) { checkBudget?.Invoke(); return world.IsWater(point); }
        bool Connector(Vector3 a,Vector3 b)=>DrySegment(a,b,Water) &&
            (BotTravelRoutePlanner.IsSafeTerminalDirect(a,b,world.GetHeight)||
             Vector3.Distance(a,b)<=2.5f&&Math.Abs(a.Z-b.Z)<=.75f);
        // Load both sides of a nearby cell seam; path chunks overlap, but their
        // node IDs and connected components belong to separate files.
        foreach(var offset in new[]{Vector3.Zero,new Vector3(15,0,0),new Vector3(-15,0,0),new Vector3(0,15,0),new Vector3(0,-15,0)})
        {
            var sample=origin+offset;
            world.Template.GetCell((int)MathF.Floor(sample.X/1024f),(int)MathF.Floor(sample.Y/1024f))?.VerifyCellLoaded();
        }
        var chunkX=(int)MathF.Floor(origin.X/256f);var chunkY=(int)MathF.Floor(origin.Y/256f);
        var graphs=world.Template.PathBaiLoader
            .Where(kv=>Math.Abs((long)kv.Key.Item1-chunkX)<=1 && Math.Abs((long)kv.Key.Item2-chunkY)<=1)
            .SelectMany(kv=>kv.Value.NetMissionReaders).Prepend(start.NetMission).Distinct().ToArray();
        var starts=graphs.SelectMany(g=>g.NodeDescriptorList.Values.Where(n=>n.Type is 2 or 4 &&
                Vector3.DistanceSquared(n.Pos,origin)<=225f)
            .OrderBy(n=>Vector3.DistanceSquared(n.Pos,origin)).Take(2)).Prepend(start).Distinct();
        starts=starts.Concat(graphs.SelectMany(g=>g.LinkDescriptorList.Where(l=>Passable(l) && Contains(l,origin))
            .SelectMany(l=>new[]{l.SourceNodeDescriptor,l.TargetNodeDescriptor}))).Distinct();
        return PlanCandidates(starts, origin, destination, Connector, allowPartial, out failure, Water, checkBudget);
    }

    internal static BotTravelRoute PlanCandidates(IEnumerable<NodeDescriptor> starts, Vector3 origin, Vector3 destination,
        Func<Vector3,Vector3,bool> connector, bool allowPartial, out string failure, Func<Vector3,bool> isWater=null, Action checkBudget=null)
    {
        failure = "start_connector_rejected";
        BotTravelRoute bestPartial=null;
        var bestDistance=Vector3.Distance(origin,destination);
        foreach(var start in starts)
        {
            checkBudget?.Invoke();
            if(!DrySegment(origin,start.Pos,isWater)||!(connector(origin,start.Pos)||EdgeConnector(start,origin)))continue;
            var route=PlanConnected(start,origin,destination,connector,allowPartial,out var rejected,isWater:isWater,checkBudget:checkBudget);
            if(route==null) { failure=rejected; continue; }
            if(route.Waypoints[^1]==destination) { failure=null; return route; }
            var distance=Vector3.Distance(route.Waypoints[^1],destination);
            if(distance<bestDistance) { bestPartial=route; bestDistance=distance; }
        }
        if(bestPartial!=null)failure=null;
        return bestPartial;
    }

    // A chunk-border node can be the nearest node to the owner but belong to a
    // disconnected component. Select endpoints from the reachable component.
    internal static BotTravelRoute PlanConnected(NodeDescriptor start, Vector3 origin, Vector3 destination,
        Func<Vector3,Vector3,bool> connector, bool allowPartial, out string failure, int budget=8192, Func<Vector3,bool> isWater=null, Action checkBudget=null)
    {
        checkBudget?.Invoke();
        failure = null;
        var edges=start.NetMission.LinkDescriptorList
            .Where(l=> { checkBudget?.Invoke(); return Passable(l)&&DrySegment(l.SourceNodeDescriptor.Pos,l.TargetNodeDescriptor.Pos,isWater); }).GroupBy(l=>l.SourceNode)
            .ToDictionary(g=>(long)g.Key,g=>g.ToArray());
        var frontier=new PriorityQueue<NodeDescriptor,float>();
        var costs=new Dictionary<long,float>{{start.Id,0f}};
        var previous=new Dictionary<long,NodeDescriptor>();
        var closed=new HashSet<long>();
        NodeDescriptor endpoint=null, partial=null;
        var bestCost=float.PositiveInfinity;
        var bestDistance=Vector3.Distance(origin,destination)-5f;
        frontier.Enqueue(start,0f);
        while(frontier.TryDequeue(out var node,out _) && budget-->0)
        {
            checkBudget?.Invoke();
            if(!closed.Add(node.Id))continue;
            var distance=Vector3.Distance(node.Pos,destination);
            if(distance<=15f && DrySegment(node.Pos,destination,isWater) && (connector(node.Pos,destination)||EdgeConnector(node,destination)))
            {
                var cost=costs[node.Id]+distance;
                if(cost<bestCost) { endpoint=node; bestCost=cost; }
            }
            if(allowPartial && distance<bestDistance) { partial=node; bestDistance=distance; }
            if(!edges.TryGetValue(node.Id,out var links))continue;
            foreach(var link in links)
            {
                var next=link.TargetNodeDescriptor;
                var cost=costs[node.Id]+Vector3.Distance(node.Pos,next.Pos);
                if(closed.Contains(next.Id)||costs.TryGetValue(next.Id,out var old)&&old<=cost)continue;
                costs[next.Id]=cost;previous[next.Id]=node;
                frontier.Enqueue(next,cost);
            }
        }
        var complete=endpoint!=null;
        endpoint ??= partial;
        if(endpoint==null)
        {
            failure=frontier.Count>0 ? $"search_budget_exhausted start={start.Id}" :
                $"no_reachable_endpoint start={start.Id} goal={destination}";
            return null;
        }
        var path=new List<Vector3>{endpoint.Pos};
        for(var node=endpoint;previous.TryGetValue(node.Id,out var parent);node=parent)path.Add(parent.Pos);
        path.Reverse();
        var points=new List<Vector3>{origin};points.AddRange(path);
        if(complete)points.Add(destination);
        return new BotTravelRoute("bai-surface",points,0,
            $"native_connected_edges endpoint={endpoint.Id} partial={!complete} z=native_surface no_corner_shortcuts") { NativeSurface=true };
    }
    internal static bool EdgeConnector(NodeDescriptor endpoint, Vector3 point)
        =>endpoint.NetMission.LinkDescriptorList.Any(l=>Passable(l) &&
            (ReferenceEquals(l.SourceNodeDescriptor,endpoint)||ReferenceEquals(l.TargetNodeDescriptor,endpoint)) && Contains(l,point));

    private static bool Contains(LinkDescriptor edge, Vector3 point)
    {
        var a=edge.SourceNodeDescriptor.Pos;var b=edge.TargetNodeDescriptor.Pos;
        var delta=new Vector2(b.X-a.X,b.Y-a.Y);
        var t=Vector2.Dot(new(point.X-a.X,point.Y-a.Y),delta)/delta.LengthSquared();
        if(t<0 || t>1)return false;
        var projected=Vector3.Lerp(a,b,t);
        return new Vector2(point.X-projected.X,point.Y-projected.Y).Length()<=.25f && Math.Abs(point.Z-projected.Z)<=.75f;
    }

    internal static BotRouteSurface FindOccupiedSurface(Character bot, Vector3 position)
    {
        var world=bot.ParentWorld;var geo=world?.Template?.GeoData;
        if(geo==null || world.IsWater(position))return null;
        var start=geo.FindСlosestToTheCurrent(bot.Transform.ZoneId,position);
        if(start==null)return null;
        var edge=start.NetMission.LinkDescriptorList.FirstOrDefault(l=>Passable(l) && Contains(l,position) &&
            DrySegment(l.SourceNodeDescriptor.Pos,l.TargetNodeDescriptor.Pos,world.IsWater));
        return edge==null ? null : new BotRouteSurface([edge.SourceNodeDescriptor.Pos,edge.TargetNodeDescriptor.Pos]);
    }

    // BAI water-depth metadata can be zero even on a riverbed edge. Use the
    // runtime water volumes at the same interpolated height the mover follows.
    internal static bool DrySegment(Vector3 a, Vector3 b, Func<Vector3,bool> isWater)
    {
        if(isWater==null)return true;
        var length=Vector3.Distance(a,b);
        if(!float.IsFinite(length))return false;
        var samples=Math.Max(1,(int)MathF.Ceiling(length));
        for(var i=0;i<=samples;i++)
            if(isWater(Vector3.Lerp(a,b,i/(float)samples)))return false;
        return true;
    }

}
