using System.Security.Claims;
using System.Text;
using DoForYou.API.Controllers;
using DoForYou.API.Data;
using DoForYou.API.Models;
using DoForYou.API.Services;
using Task = System.Threading.Tasks.Task;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DoForYou.Tests;

public sealed class ManualIdentityVerificationControllerTests
{
    private const string ConsentVersion = "identity-verification-v1";
    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["IdentityVerification:ConsentVersion"] = ConsentVersion,
            ["DFY_KYC_DOCUMENT_ENCRYPTION_KEY"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray())
        })
        .Build();

    private static AppDbContext CreateContext() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static User NewRunner(int id) => new()
    {
        Id = id, FirstName = "Test", LastName = "Runner",
        Email = $"runner{id}@example.test", NormalizedEmail = $"runner{id}@example.test",
        PasswordHash = "test-hash", UserType = "runner", EmailVerified = true
    };

    private static ManualIdentityVerificationController CreateController(AppDbContext context, int userId)
    {
        var config = Configuration();
        var controller = new ManualIdentityVerificationController(
            context,
            new IdentityDocumentEncryptionService(config),
            config,
            NullLogger<ManualIdentityVerificationController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
            }
        };
        return controller;
    }

    private static FormFile Jpeg(string name)
    {
        byte[] bytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02, 0x03];
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, name, name)
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/jpeg"
        };
    }

    [Fact]
    public void Identity_document_encryption_uses_authenticated_encryption()
    {
        var config = Configuration();
        var service = new IdentityDocumentEncryptionService(config);
        byte[] original = Encoding.UTF8.GetBytes("identity-document-test-payload");

        var encrypted = service.Encrypt(original);
        var decrypted = service.Decrypt(encrypted.Ciphertext, encrypted.Nonce, encrypted.Tag);

        Assert.True(service.IsConfigured);
        Assert.NotEqual(original, encrypted.Ciphertext);
        Assert.Equal(original, decrypted);
        encrypted.Tag[0] ^= 0x01;
        Assert.Throws<System.Security.Cryptography.AuthenticationTagMismatchException>(
            () => service.Decrypt(encrypted.Ciphertext, encrypted.Nonce, encrypted.Tag));
    }

    [Fact]
    public async Task Runner_submission_stores_only_encrypted_document_bytes_and_records_consent()
    {
        await using var context = CreateContext();
        var runner = NewRunner(101);
        context.Users.Add(runner);
        await context.SaveChangesAsync();

        var controller = CreateController(context, runner.Id);
        var front = Jpeg("front.jpg");
        var back = Jpeg("back.jpg");
        var result = await controller.Submit(new SubmitManualIdentityVerificationRequest
        {
            ConsentAccepted = true,
            ConsentVersion = ConsentVersion,
            DocumentType = "south_african_id",
            FrontDocument = front,
            BackDocument = back
        }, CancellationToken.None);

        Assert.IsType<AcceptedResult>(result.Result);
        Assert.Equal("pending", runner.IdentityVerificationStatus);
        Assert.Equal("DFY_MANUAL", runner.IdentityVerificationProvider);
        var submission = await context.IdentityVerificationSubmissions.SingleAsync();
        Assert.NotEmpty(submission.FrontCiphertext);
        Assert.NotEqual(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02, 0x03 }, submission.FrontCiphertext);
        Assert.Single(await context.UserConsentEvents.ToListAsync());
        Assert.Contains(await context.IdentityVerificationEvents.ToListAsync(), e => e.EventType == "submission_received");
    }

    [Fact]
    public async Task Administrator_approval_requires_recent_authenticator_and_updates_backend_status()
    {
        await using var context = CreateContext();
        var runner = NewRunner(102);
        var admin = new User
        {
            Id = 900, FirstName = "Admin", LastName = "User", Email = "admin@example.test",
            NormalizedEmail = "admin@example.test", PasswordHash = "test-hash", UserType = "creator",
            EmailVerified = true, Roles = "Admin", AuthenticatorTwoFactorEnabled = true,
            LastTwoFactorVerifiedAt = DateTime.UtcNow
        };
        context.Users.AddRange(runner, admin);
        var submission = new IdentityVerificationSubmission
        {
            UserId = runner.Id, DocumentType = "passport", Status = "pending",
            FrontCiphertext = [1, 2, 3], FrontNonce = new byte[12], FrontTag = new byte[16],
            FrontContentType = "image/jpeg", ConsentVersion = ConsentVersion
        };
        context.IdentityVerificationSubmissions.Add(submission);
        await context.SaveChangesAsync();

        var controller = CreateController(context, admin.Id);
        var result = await controller.Decide(submission.Id, new ManualIdentityVerificationDecisionRequest
        {
            Decision = "approve", Reason = "Document details were reviewed and matched the runner profile."
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("passed", runner.IdentityVerificationStatus);
        Assert.Equal("approved", submission.Status);
        Assert.Equal(admin.Id, submission.ReviewedByUserId);
        Assert.Contains(await context.IdentityVerificationEvents.ToListAsync(), e => e.EventType == "admin_approve");
    }

    [Fact]
    public async Task Administrator_without_recent_authenticator_cannot_approve_identity_documents()
    {
        await using var context = CreateContext();
        var runner = NewRunner(103);
        var admin = new User
        {
            Id = 901, FirstName = "Admin", LastName = "User", Email = "admin2@example.test",
            NormalizedEmail = "admin2@example.test", PasswordHash = "test-hash", UserType = "creator",
            EmailVerified = true, Roles = "Admin", AuthenticatorTwoFactorEnabled = true,
            LastTwoFactorVerifiedAt = DateTime.UtcNow.AddHours(-1)
        };
        context.Users.AddRange(runner, admin);
        var submission = new IdentityVerificationSubmission
        {
            UserId = runner.Id, DocumentType = "passport", Status = "pending",
            FrontCiphertext = [1, 2, 3], FrontNonce = new byte[12], FrontTag = new byte[16],
            FrontContentType = "image/jpeg", ConsentVersion = ConsentVersion
        };
        context.IdentityVerificationSubmissions.Add(submission);
        await context.SaveChangesAsync();

        var controller = CreateController(context, admin.Id);
        var result = await controller.Decide(submission.Id, new ManualIdentityVerificationDecisionRequest
        {
            Decision = "approve", Reason = "Document details were reviewed and matched the runner profile."
        }, CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        Assert.Equal("not_started", runner.IdentityVerificationStatus);
        Assert.Equal("pending", submission.Status);
    }
}
