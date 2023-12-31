using System;
using System.Runtime.Serialization;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

[Serializable]
public class OctoGraphQLException : Exception
{
    //
    // For guidelines regarding the creation of new exception types, see
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/cpgenref/html/cpconerrorraisinghandlingguidelines.asp
    // and
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/dncscol/html/csharp07192001.asp
    //

    public OctoGraphQLException()
    {
    }

    public OctoGraphQLException(string message) : base(message)
    {
    }

    public OctoGraphQLException(string message, Exception inner) : base(message, inner)
    {
    }
}
