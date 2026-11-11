using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Erdigo.Core;
using Erdigo.Sdk;
using Xunit;

namespace Erdigo.Tests;

public sealed class FileStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"erdigo-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string KeyFile => Path.Combine(directory, OperatingSystem.IsWindows() ? "identity.dpapi" : "identity.key");

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void KeyIsPrivateAndSurvivesRestart()
    {
        var store = new FileDeviceStore(directory);
        var identity = store.LoadOrCreateIdentity();
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyFile));
        Assert.Equal(36, new FileInfo(KeyFile).Length);

        var again = new FileDeviceStore(directory);
        Assert.Equal(identity.PublicKey, again.LoadOrCreateIdentity().PublicKey);
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void RefusesKeyFileOthersCouldRead()
    {
        new FileDeviceStore(directory).LoadOrCreateIdentity();
        File.SetUnixFileMode(KeyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var error = Assert.Throws<ErdigoException>(() => new FileDeviceStore(directory).LoadOrCreateIdentity());
        Assert.Equal(StorageProblem.Insecure, error.StorageProblem);
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void RefusesStateFolderOthersCouldRead()
    {
        _ = new FileDeviceStore(directory);
        File.SetUnixFileMode(directory, (UnixFileMode)Convert.ToInt32("755", 8));
        var error = Assert.Throws<ErdigoException>(() => new FileDeviceStore(directory));
        Assert.Equal(StorageProblem.Insecure, error.StorageProblem);
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void RefusesSymbolicLinkForKeyFile()
    {
        var store = new FileDeviceStore(directory);
        var target = Path.Combine(directory, "elsewhere");
        File.WriteAllText(target, "{}");
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.CreateSymbolicLink(KeyFile, target);
        var error = Assert.Throws<ErdigoException>(() => store.LoadOrCreateIdentity());
        Assert.Equal(StorageProblem.Insecure, error.StorageProblem);
        Assert.Equal("{}", File.ReadAllText(target));
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void RefusesSymbolicLinkForStateFolder()
    {
        var real = directory + "-real";
        Directory.CreateDirectory(real, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            Directory.CreateSymbolicLink(directory, real);
            var error = Assert.Throws<ErdigoException>(() => new FileDeviceStore(directory));
            Assert.Equal(StorageProblem.Insecure, error.StorageProblem);
        }
        finally
        {
            File.Delete(directory);
            Directory.Delete(real);
        }
    }

    /// <summary>A key that exists but cannot be used is reported, and a new one is never made in its place.</summary>
    [Fact]
    public void ADamagedKeyIsReportedAndLeftAlone()
    {
        var store = new FileDeviceStore(directory);
        store.LoadOrCreateIdentity();
        File.WriteAllBytes(KeyFile, new byte[] { 1, 2, 3 });
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(KeyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        var error = Assert.Throws<ErdigoException>(() => store.LoadOrCreateIdentity());
        Assert.Equal(StorageProblem.IdentityDamaged, error.StorageProblem);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(KeyFile));
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void AnUnreadableKeyIsReportedAndLeftAlone()
    {
        var store = new FileDeviceStore(directory);
        var identity = store.LoadOrCreateIdentity();
        var before = File.ReadAllBytes(KeyFile);
        File.SetUnixFileMode(KeyFile, UnixFileMode.None);
        try
        {
            if (Environment.UserName == "root")
            {
                return;
            }
            var error = Assert.Throws<ErdigoException>(() => store.LoadOrCreateIdentity());
            Assert.Equal(StorageProblem.IdentityUnreadable, error.StorageProblem);
        }
        finally
        {
            File.SetUnixFileMode(KeyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        Assert.Equal(before, File.ReadAllBytes(KeyFile));
        Assert.Equal(identity.PublicKey, store.LoadOrCreateIdentity().PublicKey);
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void KeyIsProtectedWithDpapiAndSurvivesRestart()
    {
        var store = new FileDeviceStore(directory);
        var identity = store.LoadOrCreateIdentity();
        var stored = File.ReadAllBytes(KeyFile);
        Assert.Equal("EDK1"u8.ToArray(), stored[..4]);
        // The private key does not appear in the file in the clear.
        var raw = identity.ExportPrivateKeyForStorage();
        Assert.False(stored.AsSpan().IndexOf(raw) >= 0);
        Assert.False(File.Exists(Path.Combine(directory, "identity.key")));

        var again = new FileDeviceStore(directory);
        Assert.Equal(identity.PublicKey, again.LoadOrCreateIdentity().PublicKey);
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void ADpapiKeyThatCannotBeDecryptedIsReportedAndLeftAlone()
    {
        var store = new FileDeviceStore(directory);
        store.LoadOrCreateIdentity();
        var stored = File.ReadAllBytes(KeyFile);
        stored[^1] ^= 0xFF;
        File.WriteAllBytes(KeyFile, stored);
        var error = Assert.Throws<ErdigoException>(() => store.LoadOrCreateIdentity());
        Assert.Equal(StorageProblem.IdentityUnreadable, error.StorageProblem);
        Assert.Equal(stored, File.ReadAllBytes(KeyFile));
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void AKeyFileOpenElsewhereIsNotReplaced()
    {
        var store = new FileDeviceStore(directory);
        store.LoadOrCreateIdentity();
        var before = File.ReadAllBytes(KeyFile);
        using (new FileStream(KeyFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Assert.Throws<ErdigoException>(() => store.LoadOrCreateIdentity());
            Assert.Equal(StorageProblem.IdentityUnreadable, error.StorageProblem);
        }
        Assert.Equal(before, File.ReadAllBytes(KeyFile));
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void StateFolderIsReadableByTheCurrentUserAlone()
    {
        _ = new FileDeviceStore(directory);
        var security = new DirectoryInfo(directory).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == System.Security.AccessControl.AccessControlType.Allow)
            .ToList();
        Assert.All(rules, rule => Assert.Equal(user, rule.IdentityReference));
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void RefusesAStateFolderEveryoneCanRead()
    {
        Directory.CreateDirectory(directory);
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null),
            System.Security.AccessControl.FileSystemRights.Read,
            System.Security.AccessControl.AccessControlType.Allow));
        info.SetAccessControl(security);
        var error = Assert.Throws<ErdigoException>(() => new FileDeviceStore(directory));
        Assert.Equal(StorageProblem.Insecure, error.StorageProblem);
    }

    [Fact]
    public void PairingsAndCountersPersist()
    {
        var store = new FileDeviceStore(directory);
        var key = Noise.PublicKey(Enumerable.Repeat((byte)7, 32).ToArray());
        var pairing = new ReceiverPairing("home", key, new[] { ItemKind.Pin }, "192.0.2.10", 49000);
        store.Save(pairing);

        var again = new FileDeviceStore(directory);
        var loaded = Assert.Single(again.Pairings());
        Assert.Equal(pairing, loaded);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(directory, "pairings.json")));
        }

        var first = again.NextConnectCounter(pairing.Fingerprint, 1000);
        var second = again.NextConnectCounter(pairing.Fingerprint, 500);
        Assert.Equal(1000UL, first);
        // The counter never goes back, even if the clock does.
        Assert.Equal(1001UL, second);
        Assert.Equal(1002UL, new FileDeviceStore(directory).NextConnectCounter(pairing.Fingerprint, 0));

        again.UpdateAddress(pairing.Fingerprint, "192.0.2.11", 49001);
        Assert.Equal("192.0.2.11", again.Pairings()[0].LastHost);
        again.RemovePairing(pairing.Fingerprint);
        Assert.Empty(again.Pairings());
    }

    [Fact]
    public void ACounterFloorTakesEffectWhenSaved()
    {
        var store = new FileDeviceStore(directory);
        var key = Noise.PublicKey(Enumerable.Repeat((byte)9, 32).ToArray());
        store.Save(new ReceiverPairing("x", key, new[] { ItemKind.Pin }, counterFloor: 5000));
        Assert.Equal(5001UL, store.NextConnectCounter(IntegrationV1.Fingerprint(key), 10));
    }

    [Fact]
    public void ADamagedPairingFileIsReported()
    {
        var store = new FileDeviceStore(directory);
        store.Save(new ReceiverPairing("x", Noise.PublicKey(new byte[32]), new[] { ItemKind.Pin }));
        var path = Path.Combine(directory, "pairings.json");
        File.WriteAllText(path, "{\"version\":1,\"pairings\":[{\"label\":\"x\"}],\"counters\":{}}");
        var error = Assert.Throws<ErdigoException>(() => store.Pairings());
        Assert.Equal(StorageProblem.StateDamaged, error.StorageProblem);
    }

    [Fact]
    public void CountersAreSafeAcrossThreads()
    {
        var store = new FileDeviceStore(directory);
        var values = new ConcurrentBag<ulong>();
        Parallel.For(0, 32, _ => values.Add(store.NextConnectCounter("x", 1)));
        Assert.Equal(32, values.Distinct().Count());
    }

    [Fact]
    public void CountersAreSafeAcrossStores()
    {
        var stores = Enumerable.Range(0, 4).Select(_ => new FileDeviceStore(directory)).ToList();
        var values = new ConcurrentBag<ulong>();
        Parallel.For(0, 32, index => values.Add(stores[index % stores.Count].NextConnectCounter("x", 1)));
        Assert.Equal(32, values.Distinct().Count());
    }

    [Fact]
    public void ManyStoresCreateOneIdentity()
    {
        var stores = Enumerable.Range(0, 8).Select(_ => new FileDeviceStore(directory)).ToList();
        var keys = new ConcurrentBag<string>();
        Parallel.ForEach(stores, store => keys.Add(Hex.Encode(store.LoadOrCreateIdentity().PublicKey)));
        Assert.Single(keys.Distinct());
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void PrivateFileRefusesAFileOthersCouldRead()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "code");
        File.WriteAllText(path, "TEST0-0NLYC-0DE00-00000");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        Assert.Equal(PrivateFileProblem.Insecure, Assert.Throws<PrivateFileException>(() => PrivateFile.ReadAllBytes(path, 1024)).Problem);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Equal("TEST0-0NLYC-0DE00-00000"u8.ToArray(), PrivateFile.ReadAllBytes(path, 1024));
        Assert.Equal(PrivateFileProblem.TooLarge, Assert.Throws<PrivateFileException>(() => PrivateFile.ReadAllBytes(path, 10)).Problem);
        Assert.Equal(PrivateFileProblem.Missing, Assert.Throws<PrivateFileException>(() => PrivateFile.ReadAllBytes(path + "-missing", 10)).Problem);
    }

    [Fact]
    public void PrivateFileWritesAFileOnlyItsOwnerCanRead()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "code");
        File.WriteAllText(path, "old and longer text");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32("644", 8));
        }
        PrivateFile.WriteAllBytes(path, "new"u8);
        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal("new"u8.ToArray(), PrivateFile.ReadAllBytes(path, 1024));
    }
}
