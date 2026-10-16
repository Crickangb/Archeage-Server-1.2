using AAEmu.Game.Models.Game.World.Transform;

namespace AAEmu.Game.Models.Game.Transfers;

public class TransferRoads
{
    public string Name { get; set; }
    public uint ZoneId { get; set; }
    public int Type { get; set; } // TemplateId -> owner_id != 0, указывает на участок начала пути и на TemplateId транспорта для этого пути
    public int CellX { get; set; }
    public int CellY { get; set; }
    public List<WorldSpawnPosition> Pos { get; set; } = [];
}

public sealed record TransferRoadPointSnapshot(
    uint WorldId,
    uint ZoneId,
    float X,
    float Y,
    float Z);

public sealed record TransferRoadPolylineSnapshot(
    uint WorldId,
    uint ZoneId,
    string Name,
    int Type,
    int CellX,
    int CellY,
    IReadOnlyList<TransferRoadPointSnapshot> Points);

public sealed record TransferRoadsSnapshot(
    long Revision,
    IReadOnlyList<TransferRoadPolylineSnapshot> Roads);
