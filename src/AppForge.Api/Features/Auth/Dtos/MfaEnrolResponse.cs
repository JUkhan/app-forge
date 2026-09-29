namespace AppForge.Api.Features.Auth.Dtos;

internal sealed record MfaEnrolResponse(string Secret, string QrCodeDataUrl, string[] BackupCodes);
