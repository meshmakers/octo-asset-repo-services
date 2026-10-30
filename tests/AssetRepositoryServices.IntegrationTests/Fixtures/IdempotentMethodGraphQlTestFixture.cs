using Meshmakers.Octo.Services.Methods.Idempotency;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     AB#7825 (OD-4): the host of <see cref="GatewayOwnedMethodGraphQlTestFixture" /> (real schema, MongoDB, real gateway,
///     dispatcher and invocation pipeline, in-process sample handler) with the idempotency filter of the invocation pipeline
///     switched on, as the production host registers it, and the in-memory store in place of the MongoDB store (the store has
///     its own tests in <c>MongoMethodIdempotencyStoreTests</c>). The fingerprint key is test-only.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class IdempotentMethodGraphQlTestFixture : GatewayOwnedMethodGraphQlTestFixture
{
    public IdempotentMethodGraphQlTestFixture()
    {
        Services.AddCkMethodIdempotency();
        Services.AddCkMethodInMemoryIdempotencyStore();
        Services.Configure<CkMethodFingerprintOptions>(o =>
        {
            o.Current = new CkMethodFingerprintKeyOptions
            {
                Id = "k-test",
                Secret = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray())
            };
        });
    }
}
