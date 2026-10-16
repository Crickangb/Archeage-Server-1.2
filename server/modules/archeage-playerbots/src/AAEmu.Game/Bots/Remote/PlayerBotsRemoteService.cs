using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using Microsoft.Extensions.Hosting;
using NLog;

namespace AAEmu.Game.Bots.Remote;

public sealed class PlayerBotsRemoteService(IBotManager botManager) : IHostedService, IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly CancellationTokenSource _cts = new();
    private HttpListener? _listener;
    private Task? _loop;
    private readonly bool _enabled = BoolEnv("AAEMU_PLAYERBOTS_REMOTE_ENABLED", false);
    private readonly string _prefix = Environment.GetEnvironmentVariable("AAEMU_PLAYERBOTS_REMOTE_PREFIX") ?? "http://127.0.0.1:17840/";
    private readonly string _apiKey = Environment.GetEnvironmentVariable("AAEMU_PLAYERBOTS_REMOTE_API_KEY") ?? string.Empty;
    private readonly HashSet<string> _allowed = (Environment.GetEnvironmentVariable("AAEMU_PLAYERBOTS_REMOTE_ALLOWED_IPS") ?? "127.0.0.1")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_enabled) { Logger.Info("PlayerBots remote controller is disabled"); return Task.CompletedTask; }
        if (_apiKey.Length < 32) throw new InvalidOperationException("AAEMU_PLAYERBOTS_REMOTE_API_KEY must contain at least 32 characters.");
        _listener = new HttpListener(); _listener.Prefixes.Add(_prefix); _listener.Start();
        _loop = Task.Run(ListenLoopAsync, CancellationToken.None);
        Logger.Info("PlayerBots remote controller listening on {0}", _prefix);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        if (_loop != null) try { await _loop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); } catch { }
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested && _listener?.IsListening == true)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch when (_cts.IsCancellationRequested || _listener?.IsListening != true) { break; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext c)
    {
        try
        {
            if (!Authorized(c)) { await Reply(c, 403, new { message = "forbidden" }); return; }
            var path = c.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (c.Request.HttpMethod == "GET" && path == "/api/v1/health") { await Reply(c, 200, new { service = "AAEmu PlayerBots Remote", version = "0.2.0", utc = DateTime.UtcNow }); return; }
            if (c.Request.HttpMethod == "GET" && path == "/api/v1/bots")
            {
                var bots = botManager.GetAllBots().Select(b => new { id=b.Id, objId=b.ObjId, name=b.Name, level=b.Level, zoneId=b.Transform.ZoneId, hp=b.Hp, maxHp=b.MaxHp, mp=b.Mp, maxMp=b.MaxMp, x=b.Transform.World.Position.X, y=b.Transform.World.Position.Y, z=b.Transform.World.Position.Z }).ToArray();
                await Reply(c, 200, new { count=bots.Length, bots }); return;
            }
            var s = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (s.Length < 5 || s[0] != "api" || s[1] != "v1" || s[2] != "bots" || !uint.TryParse(s[3], out var id)) { await Reply(c,404,new{message="not found"}); return; }
            var action=s[4];
            if (c.Request.HttpMethod == "POST" && action == "spawn") { var b=botManager.SpawnBot(id); await Reply(c,b==null?409:200,b==null?new{message="spawn failed"}:new{ok=true,id=b.Id,name=b.Name}); return; }
            if (c.Request.HttpMethod == "POST" && action == "despawn") { var ok=botManager.DespawnBot(id); await Reply(c,ok?200:404,new{ok,message=ok?"despawned":"bot not active"}); return; }
            var bot=botManager.GetBot(id); if(bot==null){await Reply(c,404,new{message="bot not active"});return;}
            if(c.Request.HttpMethod=="POST"&&action=="stop"){botManager.StopImmediately(bot);await Reply(c,200,new{ok=true});return;}
            if(c.Request.HttpMethod=="POST"&&action=="unfollow"){botManager.StopFollow(bot);await Reply(c,200,new{ok=true});return;}
            if(c.Request.HttpMethod=="POST"&&action=="move"){var r=await Body<MoveRequest>(c);if(r==null){await Reply(c,400,new{message="invalid body"});return;}botManager.SetBotDestination(bot,r.x,r.y,r.z,r.run);await Reply(c,200,new{ok=true});return;}
            if(c.Request.HttpMethod=="POST"&&action=="follow"){var r=await Body<FollowRequest>(c);if(r==null){await Reply(c,400,new{message="invalid body"});return;}var target=WorldManager.Instance.GetCharacterById(r.targetId);if(target==null){await Reply(c,404,new{message="target character not online"});return;}botManager.SetFollowTarget(bot,target,Math.Clamp(r.distance,1f,20f));await Reply(c,200,new{ok=true});return;}
            await Reply(c,404,new{message="not found"});
        }
        catch(Exception ex){Logger.Error(ex,"PlayerBots remote request failed");try{await Reply(c,500,new{message="server error"});}catch{}}
    }

    private bool Authorized(HttpListenerContext c)
    {
        var ip=c.Request.RemoteEndPoint?.Address.MapToIPv4().ToString() ?? "";
        if(_allowed.Count>0&&!_allowed.Contains(ip)) return false;
        var supplied=c.Request.Headers["X-PlayerBots-Key"] ?? "";
        var a=Encoding.UTF8.GetBytes(supplied); var b=Encoding.UTF8.GetBytes(_apiKey);
        return a.Length==b.Length && CryptographicOperations.FixedTimeEquals(a,b);
    }
    private static async Task<T?> Body<T>(HttpListenerContext c){try{return await JsonSerializer.DeserializeAsync<T>(c.Request.InputStream,new JsonSerializerOptions{PropertyNameCaseInsensitive=true});}catch{return default;}}
    private static async Task Reply(HttpListenerContext c,int status,object value){var bytes=JsonSerializer.SerializeToUtf8Bytes(value);c.Response.StatusCode=status;c.Response.ContentType="application/json";c.Response.ContentLength64=bytes.Length;await c.Response.OutputStream.WriteAsync(bytes);c.Response.Close();}
    private static bool BoolEnv(string n,bool d)=>bool.TryParse(Environment.GetEnvironmentVariable(n),out var v)?v:d;
    private sealed record MoveRequest(float x,float y,float z,bool run=true);
    private sealed record FollowRequest(uint targetId,float distance=2f);
    public void Dispose(){_cts.Cancel();_listener?.Close();_cts.Dispose();}
}
