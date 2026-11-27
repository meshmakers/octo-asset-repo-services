using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.ReleasedDocuments;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.UnitTests.ReleasedDocuments;

/// <summary>
///     The GraphQL error of the engine's released-document refusals: the engine's code, also when the refusal is wrapped,
///     and the message number of every refused item. The real lock behind the real mutations is covered by
///     <c>ReleasedDocumentLockGraphQlTests</c> (integration tests).
/// </summary>
public class ReleasedDocumentErrorTests
{
    private static OperationResult Errors(params int[] numbers)
    {
        var result = new OperationResult();
        foreach (var number in numbers)
        {
            result.AddMessage(new OperationMessage(MessageLevel.Error, "Billing/Invoice@1", number, $"refused {number}"));
        }

        return result;
    }

    [Fact]
    public void StornoRefusal_AnswersStornoRefused_WithEveryMessageNumber()
    {
        ReleasedDocumentError.TryCreateGraphQlError(new StornoRefusedException(Errors(170, 172)), out var error)
            .Should().BeTrue();

        error!.Code.Should().Be("STORNO_REFUSED");
        error.Extensions![ReleasedDocumentError.MessageNumberKey].Should().Be(170);
        var items = (List<Dictionary<string, object?>>)error.Extensions[ReleasedDocumentError.ItemsKey]!;
        items.Select(i => i[ReleasedDocumentError.MessageNumberKey]).Should().Equal(170, 172);
        items[0]["location"].Should().Be("Billing/Invoice@1");
    }

    [Fact]
    public void WrappedLockRefusal_AnswersReleasedDocumentLocked()
    {
        var locked = new ReleasedDocumentLockedException(Errors(168), []);

        ReleasedDocumentError.TryCreateGraphQlError(new InvalidOperationException("outer", locked), out var error)
            .Should().BeTrue();

        error!.Code.Should().Be("RELEASED_DOCUMENT_LOCKED");
        error.Extensions![ReleasedDocumentError.MessageNumberKey].Should().Be(168);
        error.Message.Should().StartWith("RELEASED_DOCUMENT_LOCKED: ");
    }

    [Fact]
    public void AnotherException_IsNotMapped()
    {
        ReleasedDocumentError.TryCreateGraphQlError(new InvalidOperationException("other"), out var error)
            .Should().BeFalse();
        error.Should().BeNull();
    }
}
