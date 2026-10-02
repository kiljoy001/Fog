using NinePSharp.Constants;
using NinePSharp.Fog.Namespaces.Tests.Support;
using NinePSharp.Namespaces;
using NinePSharp.Namespaces.Orleans;
using Orleans.Runtime;
using Xunit;

namespace NinePSharp.Fog.Namespaces.Tests;

public sealed class FogSharedRootTests
{
    private static readonly ResourceOperationContextModel Context = new(new ResourceOperationIdModel("root-test", 1), 1, "worker");

    public FogSharedRootTests() => FogNamespaceCluster.Start();

    private static IGrainFactory Grains => FogNamespaceCluster.Cluster.GrainFactory;

    [Fact]
    public async Task TheRootIsLaidOutByNamespaceConventionAndSurvivesDeactivation()
    {
        FogSharedRoot root = await FogSharedRoot.CreateAsync(Grains, $"layout-{Guid.NewGuid():N}");
        IFogRootGrain grain = Grains.GetGrain<IFogRootGrain>(root.ProcessGroupId);
        ResourceHandle admin = await root.CreateEntryAsync("/admin", directory: true);
        ResourceHandle ctl = await root.CreateEntryAsync("/admin/ctl", directory: false);
        Assert.NotEqual(admin.Identity, ctl.Identity);
        Assert.True(admin.IsDirectory);
        Assert.False(ctl.IsDirectory);

        FogSharedRoot fresh = await FogSharedRoot.CreateAsync(Grains, $"fresh-{Guid.NewGuid():N}");
        await Grains.GetGrain<IManagementGrain>(0).ForceActivationCollection(TimeSpan.Zero);

        // Read directly after reactivation: nothing re-initializes the tree on this path.
        Assert.Equal("/", (await Grains.GetGrain<IFogRootGrain>(fresh.ProcessGroupId).StatAsync(fresh.Root.ToModel())).Name);
        await (await FogSharedRoot.CreateAsync(Grains, root.ProcessGroupId)).CreateEntryAsync("/admin", directory: true);

        Assert.Equal(
            new[] { "admin", "bin", "mnt", "n" },
            (await grain.ReadDirectoryAsync(root.Root.ToModel())).Select(entry => entry.Name).ToArray());
        ResourceStatModel rootStat = await grain.StatAsync(root.Root.ToModel());
        Assert.Equal(("/", (uint)NinePConstants.FileMode9P.DMDIR | 0x16D, "fog"), (rootStat.Name, rootStat.Mode, rootStat.User));
        ResourceStatModel ctlStat = await grain.StatAsync(ctl.ToModel());
        Assert.Equal(("ctl", 0x124U, "fog"), (ctlStat.Name, ctlStat.Mode, ctlStat.Group));
        Assert.Equal(ctl, await root.ResolveAsync("/admin/ctl"));
        Assert.Equal(admin, (await grain.GetParentAsync(ctl.ToModel()))!.ToDomain());
        Assert.Equal(root.Root, (await grain.GetParentAsync(admin.ToModel()))!.ToDomain());
        Assert.Null(await grain.GetParentAsync(root.Root.ToModel()));

        // /mnt is the first entry after the root; its children must still name it as their parent.
        ResourceHandle mountPoint = await root.CreateEntryAsync("/mnt/x", directory: true);
        Assert.Equal(await root.ResolveAsync("/mnt"), (await grain.GetParentAsync(mountPoint.ToModel()))!.ToDomain());
        Assert.Equal(ctl, (await grain.WalkAsync(admin.ToModel(), "ctl"))!.ToDomain());
        Assert.Null(await grain.WalkAsync(admin.ToModel(), "missing"));
    }

    [Fact]
    public async Task TheRootGrainRefusesEveryMutationFromAClient()
    {
        FogSharedRoot root = await FogSharedRoot.CreateAsync(Grains, $"refuse-{Guid.NewGuid():N}");
        IFogRootGrain grain = Grains.GetGrain<IFogRootGrain>(root.ProcessGroupId);
        ResourceHandleModel mnt = (await root.ResolveAsync("/mnt")).ToModel();
        foreach (byte mode in new[]
        {
            NinePConstants.OWRITE, NinePConstants.ORDWR, (byte)(NinePConstants.OREAD | NinePConstants.OTRUNC),
            (byte)(NinePConstants.OREAD | NinePConstants.ORCLOSE),
        })
        {
            Assert.Equal("permission denied", (await Assert.ThrowsAsync<UnauthorizedAccessException>(() => grain.OpenAsync(mnt, mode, Context))).Message);
        }

        ResourceOpenHandleModel opened = await grain.OpenAsync(mnt, NinePConstants.OREAD, Context);
        Assert.Equal("root-test:1", opened.HandleId);
        Assert.NotNull(await grain.OpenAsync(mnt, NinePConstants.OEXEC, Context));
        Assert.Empty(await grain.ReadAsync(opened, 0, 64));
        await grain.ClunkAsync(opened, Context);
        Assert.Equal("permission denied", (await Assert.ThrowsAsync<UnauthorizedAccessException>(() => grain.WriteAsync(opened, 0, [1], Context))).Message);
        Assert.Equal("permission denied", (await Assert.ThrowsAsync<UnauthorizedAccessException>(() => grain.RemoveAsync(mnt, null, Context))).Message);
        Assert.Equal("permission denied", (await Assert.ThrowsAsync<ResourceCreateRejectedGrainException>(() => grain.CreateAsync(mnt, "x", true))).Message);
        Assert.Equal("permission denied", (await Assert.ThrowsAsync<ResourceCreateRejectedGrainException>(() => grain.CreateAndOpenAsync(mnt, "x", 0x1A4, NinePConstants.OWRITE, Context))).Message);
        var missing = new ResourceHandleModel(new ResourceIdentityModel(FogSharedRoot.Provider, root.ProcessGroupId, 999), QidType.QTFILE, 0);
        Assert.Equal("file does not exist", (await Assert.ThrowsAsync<FileNotFoundException>(() => grain.StatAsync(missing))).Message);
    }

    [Fact]
    public async Task EntryPathsMustBeAbsoluteNormalizedAndConsistent()
    {
        FogSharedRoot root = await FogSharedRoot.CreateAsync(Grains, $"paths-{Guid.NewGuid():N}");
        await root.CreateEntryAsync("/file", directory: false);
        Assert.Equal("The root already exists. (Parameter 'path')", (await Assert.ThrowsAsync<ArgumentException>(() => root.CreateEntryAsync("/", true))).Message);
        Assert.Equal("Shared root paths are absolute. (Parameter 'path')", (await Assert.ThrowsAsync<ArgumentException>(() => root.CreateEntryAsync("mnt/x", true))).Message);
        Assert.Equal("Shared root paths are normalized. (Parameter 'path')", (await Assert.ThrowsAsync<ArgumentException>(() => root.CreateEntryAsync("/mnt/../x", true))).Message);
        await Assert.ThrowsAsync<ArgumentException>(() => root.CreateEntryAsync("/./x", true));

        // The grain boundary keeps the message, not ParamName.
        Assert.EndsWith("(Parameter 'path')", (await Assert.ThrowsAsync<ArgumentException>(() => root.CreateEntryAsync(" ", true))).Message);
        Assert.EndsWith("(Parameter 'path')", (await Assert.ThrowsAsync<ArgumentException>(() => root.CreateEntryAsync(string.Empty, true))).Message);
        Assert.EndsWith("(Parameter 'path')", (await Assert.ThrowsAsync<ArgumentNullException>(() => root.CreateEntryAsync(null!, true))).Message);
        Assert.Equal("The parent of '/a/b' does not exist.", (await Assert.ThrowsAsync<DirectoryNotFoundException>(() => root.CreateEntryAsync("/a/b", true))).Message);
        Assert.Equal("The parent of '/file/x' is not a directory.", (await Assert.ThrowsAsync<DirectoryNotFoundException>(() => root.CreateEntryAsync("/file/x", true))).Message);
        Assert.Equal("'/file' exists with a different kind.", (await Assert.ThrowsAsync<IOException>(() => root.CreateEntryAsync("/file", true))).Message);
        Assert.Equal("'/nowhere' is not in the shared root.", (await Assert.ThrowsAsync<FileNotFoundException>(() => root.ResolveAsync("/nowhere"))).Message);
    }

    [Fact]
    public async Task ApplicationsAreMountedByOneDirectoryNameAndRemountedInPlace()
    {
        FogSharedRoot root = await FogSharedRoot.CreateAsync(Grains, $"apps-{Guid.NewGuid():N}");
        var app = new ResourceHandle(new ResourceIdentity("test-app", Guid.NewGuid().ToString("N"), 1), QidType.QTDIR);
        var file = new ResourceHandle(new ResourceIdentity("test-app", "file", 2), QidType.QTFILE);
        foreach (string invalid in new[] { "a/b", ".", ".." })
        {
            Assert.Equal(
                "An application name is one path element. (Parameter 'name')",
                (await Assert.ThrowsAsync<ArgumentException>(() => root.MountApplicationAsync(invalid, app))).Message);
        }

        await Assert.ThrowsAsync<ArgumentException>(() => root.MountApplicationAsync(" ", app));
        await Assert.ThrowsAsync<ArgumentNullException>(() => root.MountApplicationAsync("mail", null!));
        Assert.Equal(
            "An application's root is a directory. (Parameter 'applicationRoot')",
            (await Assert.ThrowsAsync<ArgumentException>(() => root.MountApplicationAsync("mail", file))).Message);

        await root.MountApplicationAsync("mail", app);
        await root.MountApplicationAsync("mail", app);

        // Reopening a root whose process group already holds mounts keeps them.
        root = await FogSharedRoot.CreateAsync(Grains, root.ProcessGroupId);
        ResourceHandle point = await root.ResolveAsync("/mnt/mail");
        Assert.Equal(point.Identity, Assert.Single(await root.MountParentsAsync()).Value);
        Assert.Equal(app.Identity, Assert.Single(await root.MountParentsAsync()).Key);
        await Assert.ThrowsAsync<ArgumentNullException>(() => FogSharedRoot.CreateAsync(null!, "x"));
        Assert.Equal("id", (await Assert.ThrowsAsync<ArgumentException>(() => FogSharedRoot.CreateAsync(Grains, " "))).ParamName);
    }
}
