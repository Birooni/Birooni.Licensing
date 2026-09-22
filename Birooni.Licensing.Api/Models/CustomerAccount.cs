using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Birooni.Licensing.Api.Models;

[Table("customer_accounts")]
public class CustomerAccount
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [EmailAddress]
    [MaxLength(200)]
    [Column("email")]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MaxLength(500)]
    [Column("password_hash")]
    public string PasswordHash { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    [Column("full_name")]
    public string FullName { get; set; } = string.Empty;

    [MaxLength(200)]
    [Column("company")]
    public string? Company { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("last_login_at")]
    public DateTimeOffset? LastLoginAt { get; set; }

    [Column("email_verified")]
    public bool EmailVerified { get; set; }

    [MaxLength(128)]
    [Column("verification_token_hash")]
    public string? VerificationTokenHash { get; set; }

    [Column("verification_expires_at")]
    public DateTimeOffset? VerificationExpiresAt { get; set; }

    [Column("verification_sent_at")]
    public DateTimeOffset? VerificationSentAt { get; set; }
}
