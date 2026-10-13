using System.Text;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.Auth;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public sealed class EdlAuthenticationEvidenceTests
{
    private const string Ack = "<data><response value=\"ACK\"/></data>";
    private const string RawAck = "<data><response value=\"ACK\" rawmode=\"true\"/></data>";
    private const string Challenge = "<data><response value=\"ACK\" sig=\"challenge&amp;42\"/></data>";
    private const string PaddedFirehose = "<?xml version='1.0'?><data></data>";

    [Fact]
    public async Task InitializeAsync_XiaomiSuccess_RecordsCompletedStageAndCanContinue()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(Encoding.UTF8.GetBytes(PaddedFirehose.PadRight(48)));
        transport.Enqueue(Encoding.UTF8.GetBytes(Challenge));
        transport.Enqueue(Encoding.UTF8.GetBytes(RawAck));
        transport.Enqueue(Encoding.UTF8.GetBytes(Ack));
        transport.Enqueue(Encoding.UTF8.GetBytes(Ack));
        using var session = CreateSession(transport);
        var provider = new XiaomiProvider((_, _, _) => Task.FromResult(Enumerable.Repeat((byte)0xA5, 256).ToArray()));
        var options = new EdlInitializationOptions { XiaomiAuthenticationProvider = provider };

        await session.InitializeAsync(null, options, TestContext.Current.CancellationToken);

        Assert.True(session.IsConfigured);
        Assert.Equal(["auth-xiaomi-rsa"], session.AuthenticationEvidence.Select(static evidence => evidence.Phase));
        Assert.Equal(CommandOutcomeKind.Completed, session.AuthenticationEvidence[0].Outcome);
        Assert.NotNull(session.AuthenticationResult);
        var result = session.AuthenticationResult!;
        Assert.Equal(EdlAuthStage.Completed, result.ReachedStage);
        Assert.Equal(EdlAuthSessionDisposition.CanContinue, result.Disposition);
        Assert.True(result.AuthenticationRequested);
        Assert.True(result.AllPhasesCompleted);
    }

    [Fact]
    public async Task InitializeAsync_XiaomiRejectedByDevice_RecordsRejectedStageAndRequiresReconnect()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(Encoding.UTF8.GetBytes(PaddedFirehose.PadRight(48)));
        transport.Enqueue(Encoding.UTF8.GetBytes("<data><response value=\"NAK\"/></data>"));
        using var session = CreateSession(transport);
        var provider = new XiaomiProvider((_, _, _) => Task.FromResult(Enumerable.Repeat((byte)0xA5, 256).ToArray()));
        var options = new EdlInitializationOptions { XiaomiAuthenticationProvider = provider };

        await Assert.ThrowsAnyAsync<EdlException>(() => session.InitializeAsync(null, options,
            TestContext.Current.CancellationToken));

        Assert.False(session.IsConfigured);
        Assert.Equal(CommandOutcomeKind.Rejected, Assert.Single(session.AuthenticationEvidence).Outcome);
        Assert.Equal("auth-xiaomi-rsa", session.AuthenticationEvidence[0].Phase);
        Assert.NotNull(session.AuthenticationResult);
        var result = session.AuthenticationResult!;
        Assert.Equal(EdlAuthStage.XiaomiRsaAuth, result.ReachedStage);
        Assert.Equal(EdlAuthSessionDisposition.RequiresReconnect, result.Disposition);
        Assert.Equal(EdlLinkStage.Failed, session.LinkStage);
    }

    [Fact]
    public async Task InitializeAsync_XiaomiChallengeDeadline_RecordsTimeoutWithoutProviderCall()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(Encoding.UTF8.GetBytes(PaddedFirehose.PadRight(48)));
        using var session = CreateSession(transport);
        var provider = new XiaomiProvider((_, _, _) => throw new InvalidOperationException("Must not sign after timeout."));
        var options = new EdlInitializationOptions
        {
            XiaomiAuthenticationProvider = provider,
            AuthenticationTimeoutMs = 50
        };

        await Assert.ThrowsAnyAsync<Exception>(() => session.InitializeAsync(null, options,
            TestContext.Current.CancellationToken));

        Assert.Equal(0, provider.Calls);
        Assert.Equal(CommandOutcomeKind.Timeout, Assert.Single(session.AuthenticationEvidence).Outcome);
        Assert.Equal(EdlAuthSessionDisposition.RequiresReconnect, session.AuthenticationResult!.Disposition);
    }

    [Fact]
    public async Task InitializeAsync_XiaomiProviderCancelled_RecordsCancelledOutcome()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(Encoding.UTF8.GetBytes(PaddedFirehose.PadRight(48)));
        transport.Enqueue(Encoding.UTF8.GetBytes(Challenge));
        using var session = CreateSession(transport);
        var provider = new XiaomiProvider((_, _, _) => Task.FromException<byte[]>(new OperationCanceledException()));
        var options = new EdlInitializationOptions { XiaomiAuthenticationProvider = provider };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.InitializeAsync(null, options,
            TestContext.Current.CancellationToken));

        Assert.Equal(CommandOutcomeKind.Cancelled, Assert.Single(session.AuthenticationEvidence).Outcome);
        Assert.Equal(EdlAuthStage.XiaomiRsaAuth, session.AuthenticationResult!.ReachedStage);
    }

    [Fact]
    public async Task InitializeAsync_DemaciaProjectSuccess_RecordsProjectStageCompleted()
    {
        using var transport = DemaciaTransport(success: true);
        transport.Enqueue(Encoding.UTF8.GetBytes(PaddedFirehose.PadRight(48)));
        using var session = CreateSession(transport);
        var provider = new ProjectProvider((_, _, _, _, _) => Task.FromResult<IReadOnlyDictionary<string, byte[]>>(
            new Dictionary<string, byte[]>
            {
                ["pk"] = Encoding.ASCII.GetBytes("0123456789ABCDEF"),
                ["token"] = Encoding.ASCII.GetBytes(new string('A', 512))
            }));
        var options = new EdlInitializationOptions
        {
            ProjectAuthentication = new EdlProjectAuthenticationOptions
            {
                Kind = EdlProjectAuthenticationKind.OnePlusDemacia,
                ProjectId = "22111",
                SerialNumber = 42,
                Provider = provider
            }
        };

        await session.InitializeAsync(null, options, TestContext.Current.CancellationToken);

        Assert.Equal(EdlAuthStage.Completed, session.AuthenticationResult!.ReachedStage);
        Assert.Equal(["auth-project"], session.AuthenticationEvidence.Select(static evidence => evidence.Phase));
        Assert.Equal(CommandOutcomeKind.Completed, session.AuthenticationEvidence[0].Outcome);
        Assert.Equal(EdlAuthSessionDisposition.CanContinue, session.AuthenticationResult.Disposition);
    }

    [Fact]
    public async Task InitializeAsync_DemaciaAckWithoutVerification_RecordsRejectedProjectStage()
    {
        using var transport = DemaciaTransport(success: false);
        transport.Enqueue(Encoding.UTF8.GetBytes(PaddedFirehose.PadRight(48)));
        using var session = CreateSession(transport);
        var provider = new ProjectProvider((_, _, _, _, _) => Task.FromResult<IReadOnlyDictionary<string, byte[]>>(
            new Dictionary<string, byte[]>
            {
                ["pk"] = Encoding.ASCII.GetBytes("0123456789ABCDEF"),
                ["token"] = Encoding.ASCII.GetBytes(new string('A', 512))
            }));
        var options = new EdlInitializationOptions
        {
            ProjectAuthentication = new EdlProjectAuthenticationOptions
            {
                Kind = EdlProjectAuthenticationKind.OnePlusDemacia,
                ProjectId = "22111",
                SerialNumber = 42,
                Provider = provider
            }
        };

        await Assert.ThrowsAnyAsync<EdlException>(() => session.InitializeAsync(null, options,
            TestContext.Current.CancellationToken));

        Assert.Equal(CommandOutcomeKind.Rejected, Assert.Single(session.AuthenticationEvidence).Outcome);
        Assert.Equal(EdlAuthStage.ProjectAuth, session.AuthenticationResult!.ReachedStage);
        Assert.Equal(EdlAuthSessionDisposition.RequiresReconnect, session.AuthenticationResult.Disposition);
        Assert.True(session.Firehose!.RequiresReconnect);
    }

    [Fact]
    public async Task InitializeAsync_SetProjModelProvider_SendsReferenceToken()
    {
        var written = new List<string>();
        using var transport = new ScriptedCommTransport
        {
            OnWrite = bytes =>
            {
                var text = Encoding.UTF8.GetString(bytes);
                written.Add(text);
                return [Encoding.UTF8.GetBytes(text.Contains("setprojmodel", StringComparison.Ordinal)
                    ? "<data><response value=\"ACK\" model_check=\"0\" auth_token_verify=\"0\"/></data>"
                    : Ack)];
            }
        };
        using var session = CreateSession(transport);
        var provider = new EdlOnePlusProjectAuthProvider(new EdlOnePlusProjectProfile
        {
            ProjectId = "18825",
            ProductionKey = "b2fad511325185e5",
            RandomPostfix = "0iyFR00pPnoqjVNL",
            VersionString = "guacamoles_21_O.22_191107",
            Family = EdlOnePlusProjectFamily.ProjectModel,
        }, EdlOnePlusReferenceVectors.FixedKey);
        var options = new EdlInitializationOptions
        {
            ProjectAuthentication = new EdlProjectAuthenticationOptions
            {
                Kind = EdlProjectAuthenticationKind.OnePlusProject,
                ProjectId = "18825",
                SerialNumber = 123456,
                Provider = provider,
                CommandTokenProvider = provider,
            }
        };

        await session.InitializeAsync(null, options, TestContext.Current.CancellationToken);

        Assert.Equal(EdlAuthStage.Completed, session.AuthenticationResult!.ReachedStage);
        Assert.Contains(written, text => text.Contains("<setprojmodel", StringComparison.Ordinal)
            && text.Contains(EdlOnePlusReferenceVectors.ProjectModelGuacamolesToken, StringComparison.Ordinal)
            && text.Contains("pk=\"FreeProgDontSale\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InitializeAsync_SetSoftwareProjModelProvider_UsesDeviceTimestampAndReferenceToken()
    {
        var written = new List<string>();
        using var transport = new ScriptedCommTransport
        {
            OnWrite = bytes =>
            {
                var text = Encoding.UTF8.GetString(bytes);
                written.Add(text);
                if (text.Contains("setprocstart", StringComparison.Ordinal))
                {
                    return [Encoding.UTF8.GetBytes(
                        "<data><response value=\"ACK\" device_timestamp=\"1729390368\"/></data>")];
                }

                return [Encoding.UTF8.GetBytes(text.Contains("setswprojmodel", StringComparison.Ordinal)
                    ? "<data><response value=\"ACK\" model_check=\"0\" auth_token_verify=\"0\"/></data>"
                    : Ack)];
            }
        };
        using var session = CreateSession(transport);
        var provider = new EdlOnePlusProjectAuthProvider(new EdlOnePlusProjectProfile
        {
            ProjectId = "f2056ae1",
            ProductionKey = "7016147d58e8c038",
            RandomPostfix = "c75oVnz8yUgLZObh",
            VersionString = "billie8_14_E.01_201028",
            Family = EdlOnePlusProjectFamily.SoftwareProjectModel,
        }, EdlOnePlusReferenceVectors.FixedKey);
        var options = new EdlInitializationOptions
        {
            ProjectAuthentication = new EdlProjectAuthenticationOptions
            {
                Kind = EdlProjectAuthenticationKind.OnePlusSoftwareProject,
                ProjectId = "f2056ae1",
                SerialNumber = 123456,
                Provider = provider,
            }
        };

        await session.InitializeAsync(null, options, TestContext.Current.CancellationToken);

        Assert.Equal(EdlAuthStage.Completed, session.AuthenticationResult!.ReachedStage);
        Assert.Contains(written, text => text.Contains("setprocstart", StringComparison.Ordinal));
        Assert.Contains(written, text => text.Contains("<setswprojmodel", StringComparison.Ordinal)
            && text.Contains(EdlOnePlusReferenceVectors.SoftwareBillie8Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task InitializeAsync_NetTypeAcknowledged_RecordsOnePlusNetTypeStageCompleted()
    {
        using var transport = new ScriptedCommTransport { OnWrite = _ => [Encoding.UTF8.GetBytes(Ack)] };
        transport.Enqueue(Encoding.UTF8.GetBytes(PaddedFirehose.PadRight(48)));
        using var session = CreateSession(transport);

        await session.InitializeAsync(null, new EdlInitializationOptions { SendOnePlusNetType = true },
            TestContext.Current.CancellationToken);

        Assert.Equal(["auth-oneplus-nettype"], session.AuthenticationEvidence.Select(static evidence => evidence.Phase));
        Assert.Equal(CommandOutcomeKind.Completed, session.AuthenticationEvidence[0].Outcome);
        Assert.Equal(EdlAuthStage.Completed, session.AuthenticationResult!.ReachedStage);
        Assert.True(session.IsConfigured);
    }

    [Fact]
    public async Task EnsureInitializedAsync_WithOptions_RoutesIntoTheRecordedStartupStage()
    {
        using var transport = new ScriptedCommTransport { OnWrite = _ => [Encoding.UTF8.GetBytes(Ack)] };
        transport.Enqueue(Encoding.UTF8.GetBytes(PaddedFirehose.PadRight(48)));
        using var session = CreateSession(transport);

        await session.EnsureInitializedAsync(null, new EdlInitializationOptions { SendOnePlusNetType = true },
            TestContext.Current.CancellationToken);

        Assert.True(session.IsConfigured);
        Assert.Equal(["auth-oneplus-nettype"],
            session.AuthenticationEvidence.Select(static evidence => evidence.Phase));
        Assert.Equal(EdlAuthStage.Completed, session.AuthenticationResult!.ReachedStage);
    }

    [Fact]
    public async Task EnsureInitializedAsync_RequirementsAfterConfiguration_FailInsteadOfBeingIgnored()
    {
        using var transport = new ScriptedCommTransport { OnWrite = _ => [Encoding.UTF8.GetBytes(Ack)] };
        transport.Enqueue(Encoding.UTF8.GetBytes(PaddedFirehose.PadRight(48)));
        using var session = CreateSession(transport);
        await session.EnsureInitializedAsync(null, TestContext.Current.CancellationToken);
        Assert.True(session.IsConfigured, $"evidence={string.Join(",", session.AuthenticationEvidence.Select(static item => item.Phase))}");

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.EnsureInitializedAsync(null,
            new EdlInitializationOptions { SendOnePlusNetType = true }, TestContext.Current.CancellationToken));
        Assert.Empty(session.AuthenticationEvidence);
    }

    [Fact]
    public async Task InitializeAsync_NetTypeRejected_RecordsRejectedAndReconnectRequired()
    {
        using var transport = new ScriptedCommTransport
        {
            OnWrite = bytes => Encoding.UTF8.GetString(bytes).Contains("SetNetType", StringComparison.Ordinal)
                ? [Encoding.UTF8.GetBytes("<data><response value=\"NAK\"/></data>")]
                : [Encoding.UTF8.GetBytes(Ack)]
        };
        transport.Enqueue(Encoding.UTF8.GetBytes(PaddedFirehose.PadRight(48)));
        using var session = CreateSession(transport);

        await Assert.ThrowsAnyAsync<EdlException>(() => session.InitializeAsync(null,
            new EdlInitializationOptions { SendOnePlusNetType = true }, TestContext.Current.CancellationToken));

        Assert.Equal(CommandOutcomeKind.Rejected, Assert.Single(session.AuthenticationEvidence).Outcome);
        Assert.Equal("auth-oneplus-nettype", session.AuthenticationEvidence[0].Phase);
        Assert.Equal(EdlAuthStage.OnePlusNetType, session.AuthenticationResult!.ReachedStage);
        Assert.Equal(EdlAuthSessionDisposition.RequiresReconnect, session.AuthenticationResult.Disposition);
    }

    [Fact]
    public async Task InitializeAsync_VipInstalledBeforeProbe_RecordsVipStageAndStopsAtIdentityGate()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(Encoding.UTF8.GetBytes(PaddedFirehose.PadRight(48)));
        using var session = CreateSession(transport);
        var options = new EdlInitializationOptions
        {
            Vip = new FirehoseVipOptions
            {
                TableProvider = new UnusedVipTables(),
                ProgramPayloadSize = 512
            },
            QueryIdentityBeforeUpload = true
        };

        await Assert.ThrowsAsync<SaharaException>(() => session.InitializeOwnedAsync(null,
            TestContext.Current.CancellationToken, options));

        Assert.Equal(CommandOutcomeKind.Completed, Assert.Single(session.AuthenticationEvidence).Outcome);
        Assert.Equal("auth-vip-enabled", session.AuthenticationEvidence[0].Phase);
        Assert.Equal(EdlAuthStage.VipEnabled, session.AuthenticationResult!.ReachedStage);
        Assert.Equal(EdlAuthSessionDisposition.RequiresReconnect, session.AuthenticationResult.Disposition);
    }

    [Fact]
    public async Task InitializeAsync_VipWithoutUsableSectorSize_FailsBeforeAnyDeviceRead()
    {
        using var transport = new ScriptedCommTransport();
        using var session = new EdlSession(transport);
        var options = new EdlInitializationOptions
        {
            Vip = new FirehoseVipOptions { TableProvider = new UnusedVipTables(), ProgramPayloadSize = 512 }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.InitializeOwnedAsync(null,
            TestContext.Current.CancellationToken, options));

        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.ReadCount);
        Assert.Empty(session.AuthenticationEvidence);
        Assert.Equal(EdlAuthStage.VipEnabled, session.AuthenticationResult!.ReachedStage);
        Assert.Equal(EdlAuthSessionDisposition.RequiresReconnect, session.AuthenticationResult.Disposition);
    }

    [Theory]
    [InlineData("xiaomi-with-project")]
    [InlineData("nettype-with-nothing")]
    [InlineData("nettype-with-project")]
    public async Task InitializeAsync_IllegalAuthenticationCombination_RejectsBeforeAnyDeviceIo(string variant)
    {
        using var transport = new ScriptedCommTransport();
        using var session = CreateSession(transport);
        var provider = new ProjectProvider((_, _, _, _, _) => throw new InvalidOperationException());
        var options = new EdlInitializationOptions
        {
            SendOnePlusNetType = variant.StartsWith("nettype", StringComparison.Ordinal),
            XiaomiAuthenticationProvider = variant == "xiaomi-with-project" ? new XiaomiProvider((_, _, _) =>
                throw new InvalidOperationException()) : null,
            ProjectAuthentication = new EdlProjectAuthenticationOptions
            {
                Kind = variant == "nettype-with-nothing"
                    ? EdlProjectAuthenticationKind.NothingProject
                    : EdlProjectAuthenticationKind.OnePlusProject,
                ProjectId = "22111",
                SerialNumber = 42,
                Provider = provider
            }
        };

        await Assert.ThrowsAsync<ArgumentException>(() => session.InitializeAsync(null, options,
            TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.ReadCount);
        Assert.Equal(0, provider.Calls);
        Assert.False(session.Firehose!.RequiresReconnect);
    }

    private static EdlSession CreateSession(ScriptedCommTransport transport) => new(transport,
        new FirehoseConfiguration
        {
            AutoDetectSectorSize = false,
            SectorSize = 512,
            ConfigureFirstAttemptTimeoutMs = 5000
        });

    private static ScriptedCommTransport DemaciaTransport(bool success) => new()
    {
        OnWrite = bytes =>
        {
            var text = Encoding.UTF8.GetString(bytes);
            if (text.Contains("demacia", StringComparison.Ordinal))
            {
                return [Encoding.UTF8.GetBytes(success
                    ? "<data><response value=\"ACK\" verify_res=\"0\"/></data>"
                    : Ack)];
            }

            return [Encoding.UTF8.GetBytes(Ack)];
        }
    };

    private sealed class XiaomiProvider(
        Func<SaharaDeviceIdentity?, ReadOnlyMemory<byte>, CancellationToken, Task<byte[]>> callback)
        : IEdlXiaomiAuthenticationProvider
    {
        internal int Calls { get; private set; }

        public Task<byte[]> CreateSignatureAsync(SaharaDeviceIdentity? identity, ReadOnlyMemory<byte> challenge,
            CancellationToken cancellationToken)
        {
            Calls++;
            return callback(identity, challenge, cancellationToken);
        }
    }

    private sealed class ProjectProvider(
        Func<EdlProjectAuthenticationKind, string, ulong, long?, CancellationToken, Task<IReadOnlyDictionary<string, byte[]>>> callback)
        : IEdlProjectAuthenticationProvider
    {
        internal int Calls { get; private set; }

        public Task<IReadOnlyDictionary<string, byte[]>> CreateCredentialsAsync(EdlProjectAuthenticationKind kind,
            string projectId, ulong serialNumber, long? deviceTimestamp, CancellationToken cancellationToken)
        {
            Calls++;
            return callback(kind, projectId, serialNumber, deviceTimestamp, cancellationToken);
        }
    }

    private sealed class UnusedVipTables : IFirehoseVipTableProvider
    {
        public Task<Stream?> OpenTableAsync(int tableIndex, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("VIP tables must not be requested in these tests.");
    }
}