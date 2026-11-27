using System;
using System.Collections.Generic;

namespace ProductCatalog.Web.Models;

public partial class AppUser
{
    public int UserId { get; set; }

    public string Username { get; set; } = null!;

    public string Email { get; set; } = null!;

    public string PasswordHash { get; set; } = null!;

    public string FullName { get; set; } = null!;

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public int RoleId { get; set; }

    public virtual ICollection<ProductAuditLog> ProductAuditLogs { get; set; } = new List<ProductAuditLog>();

    public virtual Role Role { get; set; } = null!;
}
