using System.ComponentModel;
using TermKit.Services;
using Xunit;

namespace TermKit.Tests;

public sealed class SavedCredentialTests
{
    private static string NewTarget() => "TermKit.Tests:" + Guid.NewGuid().ToString("N");

    [Theory]
    [InlineData("The server has rejected the client credentials.", true)]
    [InlineData("Logon failure: unknown user name or bad password", true)]
    [InlineData("The user name or password is incorrect.", true)]
    [InlineData("Unable to contact the server. This may be because this server does not exist, it is currently down, or it does not have the Active Directory Web Services running.", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyRejectedCredentialsStopControllerFailover(string? message, bool expected) =>
        Assert.Equal(expected, OnPremAdService.IsCredentialRejection(message));

    [Fact]
    public void CredentialsRoundTripThroughCredentialManagerAndAreRemovable()
    {
        var target = NewTarget();
        try
        {
            try { CredentialStore.Write(target, "CORP\\admin.sam", "p@ss wörd!✓"); }
            catch (Win32Exception) { return; } // Hosts without a usable logon session (some CI images) cannot store credentials.

            Assert.True(CredentialStore.Exists(target));
            Assert.True(CredentialStore.TryRead(target, out var user, out var password));
            Assert.Equal("CORP\\admin.sam", user);
            Assert.Equal("p@ss wörd!✓", password);

            CredentialStore.Write(target, "CORP\\admin.sam", "new-password");
            Assert.True(CredentialStore.TryRead(target, out _, out var replaced));
            Assert.Equal("new-password", replaced);

            CredentialStore.Delete(target);
            Assert.False(CredentialStore.Exists(target));
            Assert.False(CredentialStore.TryRead(target, out _, out _));
        }
        finally
        {
            CredentialStore.Delete(target);
        }
    }

    [Fact]
    public void MissingCredentialIsNotAnErrorAndDeleteIsIdempotent()
    {
        var target = NewTarget();
        Assert.False(CredentialStore.TryRead(target, out var user, out var password));
        Assert.Equal(string.Empty, user);
        Assert.Equal(string.Empty, password);
        CredentialStore.Delete(target);
    }

    [Fact]
    public void OversizedPasswordsAreRejectedInsteadOfTruncated()
    {
        Assert.Throws<ArgumentException>(() => CredentialStore.Write(NewTarget(), "user", new string('x', 2000)));
    }
}
