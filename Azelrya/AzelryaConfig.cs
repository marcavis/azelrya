using System;
using System.IO;
using System.Text.Json;

namespace Azelrya;

public sealed class AzelryaConfig
{
    public int HistoryLimit { get; init; } = 50;

    public static AzelryaConfig Load(string baseDirectory)
    {
        var configPath = Path.Combine(baseDirectory, "azelrya.config.json");
        if (!File.Exists(configPath))
        {
            return new AzelryaConfig();
        }

        try
        {
            var json = File.ReadAllText(configPath);
            var parsed = JsonSerializer.Deserialize<AzelryaConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (parsed is null)
            {
                return new AzelryaConfig();
            }

            return new AzelryaConfig
            {
                HistoryLimit = Math.Clamp(parsed.HistoryLimit, 1, 1000)
            };
        }
        catch
        {
            return new AzelryaConfig();
        }
    }
}
