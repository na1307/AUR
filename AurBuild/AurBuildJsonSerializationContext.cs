using System.Text.Json.Serialization;

namespace AurBuild;

[JsonSerializable(typeof(AurPkg[]))]
internal sealed partial class AurBuildJsonSerializationContext : JsonSerializerContext;
