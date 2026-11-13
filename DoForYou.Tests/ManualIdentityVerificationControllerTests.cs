using System.Security.Claims;
using System.Text;
using System.Text.Json;
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
using SkiaSharp;

namespace DoForYou.Tests;

public sealed class ManualIdentityVerificationControllerTests
{
    private sealed class TestCleanMalwareScanner : IIdentityDocumentMalwareScanner
    {
        public bool IsConfigured => true;
        public Task ScanAsync(ReadOnlyMemory<byte> document, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private const string ConsentVersion = "identity-verification-v1";
    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["IdentityVerification:ConsentVersion"] = ConsentVersion,
            ["DFY_KYC_DOCUMENT_ENCRYPTION_KEY"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray()),
            ["DFY_KYC_ID_HMAC_KEY"] = Convert.ToBase64String(Enumerable.Range(33, 32).Select(x => (byte)x).ToArray())
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
            NullLogger<ManualIdentityVerificationController>.Instance,
            imageService: new IdentityDocumentImageService(),
            malwareScanner: new TestCleanMalwareScanner(),
            documentIdService: new IdentityDocumentIdService(config));
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
        using var image = new SKBitmap(640, 480);
        using var skImage = SKImage.FromBitmap(image);
        using var encoded = skImage.Encode(SKEncodedImageFormat.Jpeg, 90);
        var bytes = encoded.ToArray();
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, name, name)
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/jpeg"
        };
    }


    [Fact]
    public async Task Identity_document_view_requires_reasoned_recent_superadmin_mfa_and_writes_audit()
    {
        await using var context = CreateContext();
        var actor = NewRunner(190);
        actor.Roles = "User,Admin,SuperAdmin";
        actor.AuthenticatorTwoFactorEnabled = true;
        actor.LastTwoFactorVerifiedAt = DateTime.UtcNow;
        var subject = NewRunner(191);
        var image = Jpeg("identity.jpg");
        var encryption = new IdentityDocumentEncryptionService(Configuration());
        var encrypted = encryption.Encrypt(await ReadFormFileAsync(image));
        var submission = new IdentityVerificationSubmission
        {
            UserId = subject.Id,
            DocumentType = "passport",
            Status = "pending",
            FrontCiphertext = encrypted.Ciphertext,
            FrontNonce = encrypted.Nonce,
            FrontTag = encrypted.Tag,
            FrontContentType = "image/jpeg",
            ConsentVersion = ConsentVersion,
            ReviewClaimedByUserId = actor.Id,
            ReviewClaimExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };
        context.Users.AddRange(actor, subject);
        context.IdentityVerificationSubmissions.Add(submission);
        await context.SaveChangesAsync();

        var controller = CreateController(context, actor.Id);
        var missingReason = await controller.GetDocument(submission.Id, "front", "", CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(missingReason);

        var result = await controller.GetDocument(
            submission.Id, "front", "Reviewing assigned identity case", CancellationToken.None);

        Assert.IsType<FileContentResult>(result);
        var audit = await context.AuditLogs.SingleAsync();
        Assert.Equal("AdminIdentityDocumentViewed", audit.Action);
        Assert.Equal(actor.Id, audit.UserId);
        Assert.Equal(submission.Id, audit.EntityId);
        Assert.Contains("Reviewing assigned identity case", audit.NewValues);
        Assert.Contains(await context.IdentityVerificationEvents.ToListAsync(),
            item => item.EventType == "admin_document_accessed" && item.ActorUserId == actor.Id);
    }

    private static async Task<byte[]> ReadFormFileAsync(IFormFile file)
    {
        await using var stream = new MemoryStream();
        await file.CopyToAsync(stream);
        return stream.ToArray();
    }

    [Fact]
    public void Identity_document_normalization_strips_metadata_and_server_watermark_changes_the_view_copy()
    {
        var service = new IdentityDocumentImageService();
        using var source = new SKBitmap(640, 480);
        using var sourceImage = SKImage.FromBitmap(source);
        using var sourceEncoded = sourceImage.Encode(SKEncodedImageFormat.Jpeg, 90);
        var normalized = service.NormalizeAndStripMetadata(sourceEncoded.ToArray(), "image/jpeg");

        using var normalizedData = SKData.CreateCopy(normalized.Bytes);
        using var normalizedCodec = SKCodec.Create(normalizedData);
        Assert.NotNull(normalizedCodec);
        Assert.Equal(SKEncodedOrigin.TopLeft, normalizedCodec!.EncodedOrigin);

        var watermarked = service.ApplyWatermark(normalized.Bytes, normalized.ContentType, "DFY CONFIDENTIAL CASE 12");
        Assert.NotEqual(normalized.Bytes, watermarked);
        using var watermarkedBitmap = SKBitmap.Decode(watermarked);
        Assert.NotNull(watermarkedBitmap);
        Assert.Equal(640, watermarkedBitmap!.Width);
        Assert.Equal(480, watermarkedBitmap.Height);
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
            DocumentType = "south_african_id_card",
            FrontDocument = front,
            BackDocument = back
        }, CancellationToken.None);

        Assert.IsType<AcceptedResult>(result.Result);
        Assert.Equal("pending", runner.IdentityVerificationStatus);
        Assert.Equal("manual_document_review", runner.IdentityVerificationProvider);
        var submission = await context.IdentityVerificationSubmissions.SingleAsync();
        Assert.NotEmpty(submission.FrontCiphertext);
        Assert.NotEqual(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02, 0x03 }, submission.FrontCiphertext);
        Assert.Single(await context.UserConsentEvents.ToListAsync());
        Assert.Contains(await context.IdentityVerificationEvents.ToListAsync(), e => e.EventType == "submission_received");
    }

    [Fact]
    public async Task Runner_can_submit_green_barcoded_id_book_photo_page_without_back_image()
    {
        await using var context = CreateContext();
        var runner = NewRunner(104);
        context.Users.Add(runner);
        await context.SaveChangesAsync();

        var controller = CreateController(context, runner.Id);
        var result = await controller.Submit(new SubmitManualIdentityVerificationRequest
        {
            ConsentAccepted = true,
            ConsentVersion = ConsentVersion,
            DocumentType = "south_african_id_book",
            FrontDocument = Jpeg("id-book-details.jpg"),
            BackDocument = null
        }, CancellationToken.None);

        Assert.IsType<AcceptedResult>(result.Result);
        var submission = await context.IdentityVerificationSubmissions.SingleAsync();
        Assert.Equal("south_african_id_book", submission.DocumentType);
        Assert.Null(submission.BackCiphertext);
    }

    [Fact]
    public async Task Smart_id_card_submission_requires_both_sides()
    {
        await using var context = CreateContext();
        var runner = NewRunner(105);
        context.Users.Add(runner);
        await context.SaveChangesAsync();

        var controller = CreateController(context, runner.Id);
        var result = await controller.Submit(new SubmitManualIdentityVerificationRequest
        {
            ConsentAccepted = true,
            ConsentVersion = ConsentVersion,
            DocumentType = "south_african_id_card",
            FrontDocument = Jpeg("smart-id-front.jpg"),
            BackDocument = null
        }, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<BadRequestObjectResult>(result.Result).StatusCode);
        Assert.Empty(await context.IdentityVerificationSubmissions.ToListAsync());
    }

    [Fact]
    public async Task Pending_queue_filters_by_reviewer_and_returns_authoritative_counts()
    {
        await using var context = CreateContext();
        var runner = NewRunner(150);
        var admin = new User
        {
            Id = 950, FirstName = "Review", LastName = "Owner", Email = "review-owner@example.test",
            NormalizedEmail = "review-owner@example.test", PasswordHash = "test-hash", UserType = "creator",
            EmailVerified = true, Roles = "Admin"
        };
        context.Users.AddRange(runner, admin);
        var priorRequest = new IdentityVerificationSubmission
        {
            UserId = runner.Id, DocumentType = "passport", Status = "more_info_requested",
            FrontCiphertext = [1], FrontNonce = new byte[12], FrontTag = new byte[16],
            FrontContentType = "image/jpeg", ConsentVersion = ConsentVersion,
            SubmittedAt = DateTime.UtcNow.AddDays(-2)
        };
        var resubmission = new IdentityVerificationSubmission
        {
            UserId = runner.Id, DocumentType = "passport", Status = "pending",
            FrontCiphertext = [2], FrontNonce = new byte[12], FrontTag = new byte[16],
            FrontContentType = "image/jpeg", ConsentVersion = ConsentVersion,
            SubmittedAt = DateTime.UtcNow.AddHours(-1), ReviewClaimedByUserId = admin.Id,
            ReviewClaimExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };
        context.IdentityVerificationSubmissions.AddRange(priorRequest, resubmission);
        await context.SaveChangesAsync();

        var result = await CreateController(context, admin.Id)
            .GetPending(1, 20, "claimed-by-me", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DoForYou.API.DTOs.ApiResponse<object>>(ok.Value);
        var data = JsonSerializer.SerializeToElement(response.Data);
        Assert.Equal(1, data.GetProperty("submissions").GetArrayLength());
        Assert.Equal(1, data.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, data.GetProperty("queueCounts").GetProperty("claimedByMe").GetInt32());
        Assert.Equal(1, data.GetProperty("queueCounts").GetProperty("resubmission").GetInt32());
        var row = data.GetProperty("submissions")[0];
        Assert.True(row.GetProperty("isClaimedByMe").GetBoolean());
        Assert.True(row.GetProperty("isResubmission").GetBoolean());
        Assert.Equal("Review Owner", row.GetProperty("claimedByName").GetString());
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
        var claim = await controller.ClaimReview(submission.Id, CancellationToken.None);
        Assert.IsType<OkObjectResult>(claim.Result);
        var result = await controller.Decide(submission.Id, new ManualIdentityVerificationDecisionRequest
        {
            Decision = "approve", VerifiedDateOfBirth = new DateTime(1980, 1, 1), RunnerNote = "Document details were reviewed and matched the runner profile.",
            InternalNote = "Second reviewer confirmed the profile name and date of birth."
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("passed", runner.IdentityVerificationStatus);
        Assert.Equal("approved", submission.Status);
        Assert.Equal(admin.Id, submission.ReviewedByUserId);
        Assert.Equal("Second reviewer confirmed the profile name and date of birth.", submission.InternalReviewNote);
        Assert.Equal("Document details were reviewed and matched the runner profile.", submission.ReviewReason);
        var audit = await context.IdentityVerificationEvents.ToListAsync();
        Assert.Contains(audit, e => e.EventType == "admin_approve" && e.ActorUserId == admin.Id && e.SubmissionId == submission.Id);
        Assert.Contains(audit, e => e.EventType == "review_claimed" && e.ActorUserId == admin.Id && e.SubmissionId == submission.Id);
    }



    [Fact]
    public async Task Reviewer_claim_is_exclusive_until_released_or_expired()
    {
        await using var context = CreateContext();
        var runner = NewRunner(107);
        var admin1 = new User
        {
            Id = 903, FirstName = "Admin", LastName = "One", Email = "admin4@example.test",
            NormalizedEmail = "admin4@example.test", PasswordHash = "test-hash", UserType = "creator",
            EmailVerified = true, Roles = "Admin", AuthenticatorTwoFactorEnabled = true,
            LastTwoFactorVerifiedAt = DateTime.UtcNow
        };
        var admin2 = new User
        {
            Id = 904, FirstName = "Admin", LastName = "Two", Email = "admin5@example.test",
            NormalizedEmail = "admin5@example.test", PasswordHash = "test-hash", UserType = "creator",
            EmailVerified = true, Roles = "Admin", AuthenticatorTwoFactorEnabled = true,
            LastTwoFactorVerifiedAt = DateTime.UtcNow
        };
        context.Users.AddRange(runner, admin1, admin2);
        var submission = new IdentityVerificationSubmission
        {
            UserId = runner.Id, DocumentType = "passport", Status = "pending",
            FrontCiphertext = [1, 2, 3], FrontNonce = new byte[12], FrontTag = new byte[16],
            FrontContentType = "image/jpeg", ConsentVersion = ConsentVersion
        };
        context.IdentityVerificationSubmissions.Add(submission);
        await context.SaveChangesAsync();

        var first = CreateController(context, admin1.Id);
        var second = CreateController(context, admin2.Id);
        Assert.IsType<OkObjectResult>((await first.ClaimReview(submission.Id, CancellationToken.None)).Result);
        Assert.IsType<OkObjectResult>((await first.HeartbeatReview(submission.Id, CancellationToken.None)).Result);
        Assert.IsType<ConflictObjectResult>((await second.ClaimReview(submission.Id, CancellationToken.None)).Result);

        var release = await first.ReleaseReview(submission.Id, CancellationToken.None);
        Assert.IsType<OkObjectResult>(release.Result);
        Assert.IsType<OkObjectResult>((await second.ClaimReview(submission.Id, CancellationToken.None)).Result);
    }


    [Fact]
    public async Task Releasing_KYC_review_claim_requires_recent_authenticator_verification()
    {
        await using var context = CreateContext();
        var runner = NewRunner(115);
        var admin = new User
        {
            Id = 915, FirstName = "Admin", LastName = "Reviewer", Email = "admin-reviewer@example.test",
            NormalizedEmail = "admin-reviewer@example.test", PasswordHash = "test-hash", UserType = "creator",
            EmailVerified = true, Roles = "Admin", AuthenticatorTwoFactorEnabled = true,
            LastTwoFactorVerifiedAt = DateTime.UtcNow.AddHours(-1)
        };
        var submission = new IdentityVerificationSubmission
        {
            UserId = runner.Id, DocumentType = "passport", Status = "pending",
            FrontCiphertext = [1, 2, 3], FrontNonce = new byte[12], FrontTag = new byte[16],
            FrontContentType = "image/jpeg", ConsentVersion = ConsentVersion,
            ReviewClaimedByUserId = admin.Id, ReviewClaimExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };
        context.Users.AddRange(runner, admin);
        context.IdentityVerificationSubmissions.Add(submission);
        await context.SaveChangesAsync();

        var result = await CreateController(context, admin.Id)
            .ReleaseReview(submission.Id, CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        Assert.Equal(admin.Id, submission.ReviewClaimedByUserId);
        Assert.Empty(await context.AuditLogs.ToListAsync());
        Assert.Empty(await context.IdentityVerificationEvents.ToListAsync());
    }

    [Fact]
    public async Task Duplicate_South_African_ID_is_flagged_for_investigation_not_auto_rejected()
    {
        await using var context = CreateContext();
        const string validId = "8001015009087";
        var policy = new UserPolicyService();
        Assert.True(policy.TryGetAdultDateOfBirthFromSouthAfricanId(validId, out var dob));
        var idService = new IdentityDocumentIdService(Configuration());
        var duplicateOwner = NewRunner(108);
        duplicateOwner.IdentityDocumentIdHmac = idService.ComputeHmac(validId);
        var runner = NewRunner(109);
        var admin = new User
        {
            Id = 905, FirstName = "Admin", LastName = "User", Email = "admin6@example.test",
            NormalizedEmail = "admin6@example.test", PasswordHash = "test-hash", UserType = "creator",
            EmailVerified = true, Roles = "Admin", AuthenticatorTwoFactorEnabled = true,
            LastTwoFactorVerifiedAt = DateTime.UtcNow
        };
        context.Users.AddRange(duplicateOwner, runner, admin);
        var submission = new IdentityVerificationSubmission
        {
            UserId = runner.Id, DocumentType = "south_african_id_card", Status = "pending",
            FrontCiphertext = [1, 2, 3], FrontNonce = new byte[12], FrontTag = new byte[16],
            FrontContentType = "image/jpeg", ConsentVersion = ConsentVersion
        };
        context.IdentityVerificationSubmissions.Add(submission);
        await context.SaveChangesAsync();

        var controller = CreateController(context, admin.Id);
        Assert.IsType<OkObjectResult>((await controller.ClaimReview(submission.Id, CancellationToken.None)).Result);
        var result = await controller.Decide(submission.Id, new ManualIdentityVerificationDecisionRequest
        {
            Decision = "approve",
            VerifiedDateOfBirth = dob,
            SouthAfricanIdNumber = validId,
            RunnerNote = "Document details were reviewed and matched the runner profile."
        }, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal("pending", submission.Status);
        Assert.True(submission.DuplicateIdDetected);
        Assert.Equal(idService.ComputeHmac(validId), submission.DocumentIdHmac);
        Assert.Equal("not_started", runner.IdentityVerificationStatus);
        Assert.Contains(await context.IdentityVerificationEvents.ToListAsync(),
            e => e.EventType == "duplicate_id_flagged" && e.ActorUserId == admin.Id && e.SubmissionId == submission.Id);
    }

    [Fact]
    public async Task Administrator_cannot_approve_runner_under_18()
    {
        await using var context = CreateContext();
        var runner = NewRunner(106);
        var admin = new User
        {
            Id = 902, FirstName = "Admin", LastName = "User", Email = "admin3@example.test",
            NormalizedEmail = "admin3@example.test", PasswordHash = "test-hash", UserType = "creator",
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
            Decision = "approve",
            VerifiedDateOfBirth = DateTime.UtcNow.Date.AddYears(-17),
            RunnerNote = "Document details were reviewed and matched the runner profile."
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("not_started", runner.IdentityVerificationStatus);
        Assert.Equal("pending", submission.Status);
        Assert.Null(runner.DateOfBirth);
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
            Decision = "approve", RunnerNote = "Document details were reviewed and matched the runner profile."
        }, CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        Assert.Equal("not_started", runner.IdentityVerificationStatus);
        Assert.Equal("pending", submission.Status);
    }
}
