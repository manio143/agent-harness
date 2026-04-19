using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Agent.Acp.Schema;
using Agent.Harness;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using Agent.Server;
using FluentAssertions;
using Xunit;

namespace Agent.Server.Tests;

public sealed class McpSessionToolCacheTests
{
    [Fact]
    public async Task EnsureDiscoveredOnLoadAsync_WhenNoConfigFile_DoesNotDiscover()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcp-cache", Guid.NewGuid().ToString("N"));
        var store = new JsonlSessionStore(root);
        store.CreateNew("s1", new SessionMetadata("s1", "/tmp", null, "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z"));

        var discovery = new CapturingDiscovery();
        var cache = new McpSessionToolCache(discovery);

        await cache.EnsureDiscoveredOnLoadAsync(store, "s1", CancellationToken.None);

        discovery.Calls.Should().Be(0);
        cache.TryGet("s1", out _).Should().BeFalse();
    }

    [Fact]
    public async Task EnsureDiscoveredOnLoadAsync_WhenConfigPresent_DiscoversOnce_AndCaches()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcp-cache", Guid.NewGuid().ToString("N"));
        var store = new JsonlSessionStore(root);
        store.CreateNew("s1", new SessionMetadata("s1", "/tmp", null, "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z"));

        Directory.CreateDirectory(Path.Combine(root, "s1"));
        var cfgPath = Path.Combine(root, "s1", "mcpServers.json");
        File.WriteAllText(cfgPath, JsonSerializer.Serialize(new List<McpServer>
        {
            new() { AdditionalProperties = { ["stdio"] = new object() } },
        }));

        var discovery = new CapturingDiscovery();
        var cache = new McpSessionToolCache(discovery);

        await cache.EnsureDiscoveredOnLoadAsync(store, "s1", CancellationToken.None);
        await cache.EnsureDiscoveredOnLoadAsync(store, "s1", CancellationToken.None);

        discovery.Calls.Should().Be(1);
        cache.TryGet("s1", out var v).Should().BeTrue();
        v.Tools.Should().NotBeEmpty();
    }

    [Fact]
    public async Task EnsureDiscoveredOnLoadAsync_WhenConfigInvalid_Throws()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcp-cache", Guid.NewGuid().ToString("N"));
        var store = new JsonlSessionStore(root);
        store.CreateNew("s1", new SessionMetadata("s1", "/tmp", null, "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z"));

        Directory.CreateDirectory(Path.Combine(root, "s1"));
        var cfgPath = Path.Combine(root, "s1", "mcpServers.json");
        File.WriteAllText(cfgPath, "{not json");

        var cache = new McpSessionToolCache(new CapturingDiscovery());

        await Assert.ThrowsAnyAsync<Exception>(() => cache.EnsureDiscoveredOnLoadAsync(store, "s1", CancellationToken.None));
    }

    private sealed class CapturingDiscovery : IMcpDiscovery
    {
        public int Calls { get; private set; }

        public Task<(ImmutableArray<ToolDefinition> Tools, IMcpToolInvoker Invoker)> DiscoverAsync(NewSessionRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult((
                Tools: ImmutableArray.Create(new ToolDefinition("mcp_echo", "", JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone())),
                Invoker: (IMcpToolInvoker)NullMcpToolInvoker.Instance));
        }
    }
}
