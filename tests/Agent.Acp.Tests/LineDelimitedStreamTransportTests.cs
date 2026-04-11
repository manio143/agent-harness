using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Agent.Acp.Protocol;
using Agent.Acp.Transport;

namespace Agent.Acp.Tests;

public class LineDelimitedStreamTransportTests
{
    [Fact]
    public async Task SendMessageAsync_Writes_Single_Line_Delimited_Json()
    {
        var inputPipe = new Pipe();
        var outputPipe = new Pipe();

        await using var transport = new LineDelimitedStreamTransport(
            input: inputPipe.Reader.AsStream(),
            output: outputPipe.Writer.AsStream(),
            name: "test");

        await transport.SendMessageAsync(new JsonRpcRequest
        {
            Id = JsonDocument.Parse("1").RootElement,
            Method = "ping",
            Params = null,
        });

        // Read the outbound bytes.
        var read = await outputPipe.Reader.ReadAsync();
        var bytes = read.Buffer.ToArray();
        var text = Encoding.UTF8.GetString(bytes);
        outputPipe.Reader.AdvanceTo(read.Buffer.End);

        Assert.EndsWith("\n", text);

        var line = text.TrimEnd('\n');
        var msg = JsonSerializer.Deserialize<JsonRpcMessage>(line, AcpJson.Options);
        Assert.NotNull(msg);
        Assert.IsType<JsonRpcRequest>(msg);
        Assert.Equal("ping", ((JsonRpcRequest)msg).Method);
    }

    [Fact]
    public async Task ReadLoop_Parses_Newline_Delimited_JsonRpc_Messages()
    {
        var inputPipe = new Pipe();
        var outputPipe = new Pipe();

        await using var transport = new LineDelimitedStreamTransport(
            input: inputPipe.Reader.AsStream(),
            output: outputPipe.Writer.AsStream(),
            name: "test");

        var m1 = JsonSerializer.Serialize(new JsonRpcRequest { Id = JsonDocument.Parse("1").RootElement, Method = "m1" }, AcpJson.Options);
        var m2 = JsonSerializer.Serialize(new JsonRpcRequest { Id = JsonDocument.Parse("2").RootElement, Method = "m2" }, AcpJson.Options);

        await inputPipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(m1 + "\n" + m2 + "\n"));
        await inputPipe.Writer.FlushAsync();

        var r1 = await transport.MessageReader.ReadAsync();
        var r2 = await transport.MessageReader.ReadAsync();

        Assert.Equal("m1", ((JsonRpcRequest)r1).Method);
        Assert.Equal("m2", ((JsonRpcRequest)r2).Method);
    }
}
