using NUnit.Framework;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Services;

namespace UnitTests.Tests.Services;

[TestFixture]
public class WebRegisterMailServiceTests
{
    [Test]
    public void ResolveAdminRecipients_UsesOnlyValidConfiguredAddresses()
    {
        var config = CreateConfig();
        config.WebRegisterConfig!.AdminNotificationEmails =
        [
            " admin@example.com ",
            "invalid-address",
            "ADMIN@example.com",
            "second@example.com",
        ];

        var result = WebRegisterMailService.ResolveAdminRecipients(config);

        Assert.That(result, Is.EqualTo(new[] { "admin@example.com", "second@example.com" }));
    }

    [Test]
    public void ResolveAdminRecipients_FallsBackToSenderWhenConfiguredAddressesAreInvalid()
    {
        var config = CreateConfig();
        config.WebRegisterConfig!.AdminNotificationEmails = ["invalid-address"];

        var result = WebRegisterMailService.ResolveAdminRecipients(config);

        Assert.That(result, Is.EqualTo(new[] { "sender@example.com" }));
    }

    [Test]
    public void ResolveAdminRecipients_FallsBackToEmailUsernameWhenSenderIsInvalid()
    {
        var config = CreateConfig();
        config.WebRegisterConfig!.AdminNotificationEmails = [];
        config.SmtpConfig!.SenderEmail = "invalid-address";
        config.SmtpConfig.Username = "smtp-admin@example.com";

        var result = WebRegisterMailService.ResolveAdminRecipients(config);

        Assert.That(result, Is.EqualTo(new[] { "smtp-admin@example.com" }));
    }

    private static WebRegisterModConfig CreateConfig() => new()
    {
        SmtpConfig = new WebRegisterSmtpConfig
        {
            Server = "smtp.example.com",
            SenderEmail = "sender@example.com",
            Username = "smtp-user",
        },
        WebRegisterConfig = new WebRegisterConfig(),
    };
}
