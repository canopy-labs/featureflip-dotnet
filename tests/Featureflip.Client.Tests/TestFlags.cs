using System.Collections.Generic;
using System.Text.Json;
using Featureflip.Client.Internal.Models;

namespace Featureflip.Client.Tests;

internal static class TestFlags
{
    public static FlagConfiguration BoolFlag(string key, bool enabled, string variation)
    {
        return new FlagConfiguration
        {
            Key = key,
            Version = 1,
            Type = FlagType.Boolean,
            Enabled = enabled,
            Variations = new List<Variation>
            {
                new Variation { Key = "on", Value = JsonSerializer.SerializeToElement(true) },
                new Variation { Key = "off", Value = JsonSerializer.SerializeToElement(false) }
            },
            Rules = new List<TargetingRule>(),
            Fallthrough = new ServeConfig { Type = ServeType.Fixed, Variation = variation },
            OffVariation = "off"
        };
    }
}
