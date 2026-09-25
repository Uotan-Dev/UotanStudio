using Xunit;

namespace UotanStudio.Tests.TestInfrastructure;

[CollectionDefinition("Log pipeline", DisableParallelization = true)]
public sealed class LogPipelineCollection;

[CollectionDefinition("DebugBridgeIdentitySettings", DisableParallelization = true)]
public sealed class DebugBridgeIdentitySettingsCollection;