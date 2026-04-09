using System.Text.Json.Serialization;

namespace LMP;

/// <summary>
/// Source-generated JSON serializer context for <see cref="ModuleState"/>.
/// AOT-safe, trimming-safe, zero-reflection serialization.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ModuleState))]
public partial class ModuleStateSerializerContext : JsonSerializerContext;
