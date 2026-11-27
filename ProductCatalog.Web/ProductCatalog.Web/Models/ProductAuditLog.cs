using System;
using System.Collections.Generic;

namespace ProductCatalog.Web.Models;

public partial class ProductAuditLog
{
    public int LogId { get; set; }

    public int ProductId { get; set; }

    public int? ChangedBy { get; set; }

    public decimal? OldPrice { get; set; }

    public decimal? NewPrice { get; set; }

    public int? OldStock { get; set; }

    public int? NewStock { get; set; }

    public DateTime ChangedAt { get; set; }

    public virtual AppUser? ChangedByNavigation { get; set; }

    public virtual Product Product { get; set; } = null!;
}
