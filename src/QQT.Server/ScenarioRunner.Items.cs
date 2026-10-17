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
}
