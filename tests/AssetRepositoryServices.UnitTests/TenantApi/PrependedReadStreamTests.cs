using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;
using Xunit;

namespace AssetRepositoryServices.UnitTests.TenantApi;

public class PrependedReadStreamTests
{
    [Fact]
    public void Read_YieldsPrefixThenInnerStream()
    {
        var prefix = new byte[] { 1, 2, 3 };
        var inner = new MemoryStream(new byte[] { 4, 5, 6, 7 });
        using var stream = new PrependedReadStream(prefix, inner);

        var output = new byte[16];
        var first = stream.Read(output, 0, 2);
        var second = stream.Read(output, first, 2);
        var third = stream.Read(output, first + second, output.Length - (first + second));
        var done = stream.Read(output, first + second + third, 4);

        first.Should().Be(2);
        second.Should().Be(1); // remainder of prefix
        third.Should().Be(4); // entire inner stream
        done.Should().Be(0);
        output[..(first + second + third)].Should().Equal(1, 2, 3, 4, 5, 6, 7);
    }

    [Fact]
    public async Task ReadAsync_YieldsPrefixThenInnerStream()
    {
        var prefix = new byte[] { 0xAA, 0xBB };
        var inner = new MemoryStream(new byte[] { 0xCC, 0xDD });
        await using var stream = new PrependedReadStream(prefix, inner);

        var dest = new byte[4];
        var read = 0;
        while (read < dest.Length)
        {
            var n = await stream.ReadAsync(dest.AsMemory(read), TestContext.Current.CancellationToken);
            if (n == 0) break;
            read += n;
        }

        read.Should().Be(4);
        dest.Should().Equal(0xAA, 0xBB, 0xCC, 0xDD);
    }

    [Fact]
    public void Dispose_DisposesInnerStream()
    {
        var inner = new MemoryStream(new byte[] { 1, 2 });
        var stream = new PrependedReadStream(Array.Empty<byte>(), inner);
        stream.Dispose();

        var act = () => inner.ReadByte();
        act.Should().Throw<ObjectDisposedException>();
    }
}
