using System.Collections;
using System.Management.Automation;
using Migration4Dataverse.Engine.PowerShell;

namespace Migration4Dataverse.Engine.Tests.PowerShell;

public sealed class DataverseLookupReferenceResolverTests
{
    [Fact]
    public void DirectGuidReferenceIsTyped()
    {
        var targetId = Guid.NewGuid();
        var reference = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["TableName"] = "pricelevel",
            ["Id"] = targetId.ToString("D")
        };

        var resolved = Assert.IsType<PSObject>(DataverseLookupReferenceResolver.Resolve(reference));

        Assert.Equal("pricelevel", resolved.Properties["TableName"].Value);
        Assert.Equal(targetId, resolved.Properties["Id"].Value);
    }

    [Fact]
    public void RejectsAlternateKeyLookupReferences()
    {
        var reference = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["TableName"] = "account",
            ["Id"] = "001source",
            ["KeyColumn"] = "new_salesforceid"
        };

        var exception = Assert.Throws<InvalidDataException>(
            () => DataverseLookupReferenceResolver.Resolve(reference));

        Assert.Contains("only TableName and Id", exception.Message);
    }

    [Fact]
    public void RejectsNonGuidLookupIds()
    {
        var reference = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["TableName"] = "account",
            ["Id"] = "001source"
        };

        Assert.Throws<InvalidDataException>(() => DataverseLookupReferenceResolver.Resolve(reference));
    }
}
