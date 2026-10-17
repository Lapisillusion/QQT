using System.Text.Json;
using QQT.Core;

namespace QQT.Server;

public sealed partial class ScenarioRunner
{
    private void ConfigureWorldItems(ScenarioScript script)
    {
        if (script.Items is { } items) { _world.ConfigureItems(items); return; }
        var section = _configuration.GetSection("Items");
        if (!section.Exists()) { _world.ConfigureItems(null); return; }
        // 配置映射复用已有消息池；JSON 只在同步初始化期间借用，解析完成后才能归还。
        var buffer = _messages.Get();
        try
        {
            var writer = buffer.Writer;
            writer.WriteStartObject(); writer.WriteString("mode", section["mode"]);
            if (section["seed"] is { } seed) writer.WriteNumber("seed", int.Parse(seed, System.Globalization.CultureInfo.InvariantCulture));
            var counts = section.GetSection("counts");
            if (counts.Exists())
            {
                writer.WriteStartObject("counts");
                foreach (var value in counts.GetChildren())
                {
                    ItemNames.Parse(value.Key);
                    writer.WriteNumber(value.Key, int.Parse(value.Value ?? throw new InvalidDataException("缺少道具数量。"), System.Globalization.CultureInfo.InvariantCulture));
                }
                writer.WriteEndObject();
            }
            var placements = section.GetSection("placements");
            if (section["mode"] == "fixed" || placements.Exists())
            {
                writer.WriteStartArray("placements");
                foreach (var row in placements.GetChildren())
                {
                    writer.WriteStartObject(); writer.WriteString("type", row["type"]);
                    writer.WriteStartObject("position");
                    writer.WriteNumber("x", int.Parse(row["position:x"] ?? throw new InvalidDataException("缺少道具坐标。"), System.Globalization.CultureInfo.InvariantCulture));
                    writer.WriteNumber("y", int.Parse(row["position:y"] ?? throw new InvalidDataException("缺少道具坐标。"), System.Globalization.CultureInfo.InvariantCulture));
                    writer.WriteEndObject(); writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject(); writer.Flush();
            using var document = JsonDocument.Parse(buffer.Written);
            _world.ConfigureItems(document.RootElement);
        }
        finally { _messages.Return(buffer); }
    }
    private static void WriteGains(Utf8JsonWriter w, string name, ItemGains gains)
    {
        w.WriteStartObject(name);
        for (var type = ItemType.Speed; type <= ItemType.Vision; type++) w.WriteNumber(ItemNames.Name(type), gains.Get(type));
        w.WriteEndObject();
    }
    private static void WriteAttributes(Utf8JsonWriter w, PlayerAttributes values)
    {
        w.WriteStartObject("attributes"); w.WriteNumber("speed", values.Speed); w.WriteNumber("capacity", values.Capacity);
        w.WriteNumber("range", values.Range); w.WriteNumber("vision", values.Vision); w.WriteEndObject();
    }
    private void WriteItems(Utf8JsonWriter w)
    {
        w.WriteStartArray("items");
        for (int i = 0; i < GameSimulation.CellCount; i++)
        {
            var item = _world.Items[i]; if (item.Type == ItemType.None) continue;
            w.WriteStartObject(); w.WriteNumber("id", i + 1); WritePosition(w, "position", new(i % GameSimulation.Size, i / GameSimulation.Size));
            w.WriteString("type", ItemNames.Name(item.Type)); w.WriteString("status", item.Status switch { ItemStatus.Hidden => "hidden", ItemStatus.Exposed => "exposed", ItemStatus.Destroyed => "destroyed", ItemStatus.Picked => "picked", _ => "none" }); w.WriteNumber("effect", 1); w.WriteEndObject();
        }
        w.WriteEndArray(); w.WriteStartObject("itemAllocation"); w.WriteString("mode", _world.ItemMode);
        if (_world.ItemSeed is { } seed) w.WriteNumber("seed", seed); else w.WriteNull("seed");
        WriteGains(w, "requested", _world.RequestedItems); WriteGains(w, "generated", _world.GeneratedItems);
        WriteGains(w, "skipped", new(_world.RequestedItems.Speed - _world.GeneratedItems.Speed, _world.RequestedItems.Capacity - _world.GeneratedItems.Capacity,
            _world.RequestedItems.Range - _world.GeneratedItems.Range, _world.RequestedItems.Vision - _world.GeneratedItems.Vision));
        w.WriteEndObject();
    }
}
