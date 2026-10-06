using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Xunit;

namespace AssetRepositoryServices.UnitTests.Architecture;

/// <summary>
///     AB#5528 (concept §3.7): the asset repository never decrypts a Secret attribute. There is no public decrypt
///     endpoint in v1; every read projects "is set" only. This test scans the compiled service assembly for
///     calls to the decrypting members of the engine (<c>ISecretAttributeProtector.Unprotect</c> and the
///     <c>GetSecretPlaintext</c> extension) and fails for every caller that is not on the allowlist.
/// </summary>
/// <remarks>
///     The allowlist is empty on purpose. Adding an entry ("Namespace.Type::Method") needs a reviewed reason
///     in the concept - the asset repository exposes data to API clients, and any server-side plaintext here is
///     one serialization step away from a response.
/// </remarks>
public class SecretDecryptionArchitectureTests
{
    private static readonly IReadOnlySet<string> Allowlist = new HashSet<string>(StringComparer.Ordinal);

    private static readonly (string TypeName, string MemberName)[] ForbiddenMembers =
    [
        ("ISecretAttributeProtector", "Unprotect"),
        ("SecretAttributeProtector", "Unprotect"),
        ("SecretAttributeExtensions", "GetSecretPlaintext")
    ];

    [Fact]
    public void ServiceAssembly_NeverCallsSecretDecryption_OutsideTheAllowlist()
    {
        var callers = FindCallers(ForbiddenMembers);

        callers.Where(c => !Allowlist.Contains(c)).Should().BeEmpty(
            "the asset repository must never decrypt Secret attributes (AB#5528 concept §3.7); found callers: {0}",
            string.Join(", ", callers));
    }

    [Fact]
    public void Scanner_FindsKnownMemberReferences()
    {
        // Guards the scan itself: if the metadata walk silently found nothing, the test above would pass
        // for the wrong reason. GetAttributeValueOrDefault is called by the GraphQL attribute resolvers.
        var callers = FindCallers([("RtTypeWithAttributes", "GetAttributeValueOrDefault")]);

        callers.Should().NotBeEmpty();
    }

    private static List<string> FindCallers(IReadOnlyCollection<(string TypeName, string MemberName)> members)
    {
        var assemblyPath = typeof(OctoSchema).Assembly.Location;
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var reader = peReader.GetMetadataReader();

        var forbiddenTokens = new HashSet<int>();
        foreach (var handle in reader.MemberReferences)
        {
            var memberReference = reader.GetMemberReference(handle);
            var name = reader.GetString(memberReference.Name);
            var parentName = GetParentTypeName(reader, memberReference.Parent);
            if (members.Any(m => m.MemberName == name && m.TypeName == parentName))
            {
                forbiddenTokens.Add(MetadataTokens.GetToken(handle));
            }
        }

        // Generic instantiations (MethodSpec) of a forbidden member reference count as well.
        foreach (var handle in Enumerable.Range(1, reader.GetTableRowCount(TableIndex.MethodSpec))
                     .Select(MetadataTokens.MethodSpecificationHandle))
        {
            var methodSpec = reader.GetMethodSpecification(handle);
            if (methodSpec.Method.Kind == HandleKind.MemberReference &&
                forbiddenTokens.Contains(MetadataTokens.GetToken(methodSpec.Method)))
            {
                forbiddenTokens.Add(MetadataTokens.GetToken(handle));
            }
        }

        var callers = new List<string>();
        if (forbiddenTokens.Count == 0)
        {
            return callers;
        }

        foreach (var methodHandle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(methodHandle);
            if (method.RelativeVirtualAddress == 0)
            {
                continue;
            }

            var body = peReader.GetMethodBody(method.RelativeVirtualAddress);
            var il = body.GetILBytes();
            if (il == null || !ContainsCallToken(il, forbiddenTokens))
            {
                continue;
            }

            var declaringType = reader.GetTypeDefinition(method.GetDeclaringType());
            var typeName = $"{reader.GetString(declaringType.Namespace)}.{reader.GetString(declaringType.Name)}";
            callers.Add($"{typeName}::{reader.GetString(method.Name)}");
        }

        return callers;
    }

    private static bool ContainsCallToken(byte[] il, IReadOnlySet<int> tokens)
    {
        // call (0x28), callvirt (0x6F), ldftn (0xFE 0x06), ldvirtftn (0xFE 0x07) followed by a 4-byte token.
        for (var i = 0; i < il.Length - 4; i++)
        {
            var opcode = il[i];
            var isCall = opcode is 0x28 or 0x6F ||
                         (opcode == 0xFE && i + 5 < il.Length && il[i + 1] is 0x06 or 0x07);
            if (!isCall)
            {
                continue;
            }

            var tokenOffset = opcode == 0xFE ? i + 2 : i + 1;
            if (tokenOffset + 4 > il.Length)
            {
                continue;
            }

            var token = BitConverter.ToInt32(il, tokenOffset);
            if (tokens.Contains(token))
            {
                return true;
            }
        }

        return false;
    }

    private static string? GetParentTypeName(MetadataReader reader, EntityHandle parent)
    {
        switch (parent.Kind)
        {
            case HandleKind.TypeReference:
                return reader.GetString(reader.GetTypeReference((TypeReferenceHandle)parent).Name);
            case HandleKind.TypeDefinition:
                return reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)parent).Name);
            case HandleKind.TypeSpecification:
                // Generic instantiation: decode the blob's first type handle.
                var typeSpec = reader.GetTypeSpecification((TypeSpecificationHandle)parent);
                var blobReader = reader.GetBlobReader(typeSpec.Signature);
                if (blobReader.ReadSignatureTypeCode() == SignatureTypeCode.GenericTypeInstance)
                {
                    blobReader.ReadSignatureTypeCode();
                    var genericType = blobReader.ReadTypeHandle();
                    return GetParentTypeName(reader, genericType);
                }

                return null;
            default:
                return null;
        }
    }
}
