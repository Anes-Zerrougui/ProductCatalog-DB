using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProductCatalog.Web.Models;
using ProductCatalog.Web.Models.Data;
using ProductCatalog.Web.Models.ViewModels;
using ProductCatalog.Web.Security;

namespace ProductCatalog.Web.Controllers;

// Everyone signed in can list, search and view. Admin and Editor can create, edit, delete and adjust stock.
[Authorize]
public class ProductsController : Controller
{
    private readonly AppDbContext _db;

    public ProductsController(AppDbContext db)
    {
        _db = db;
    }

    // ---------- LIST + SEARCH + FILTER + SORT + PAGING ----------
    // GET: /Products?search=dell&groupId=1&status=Active&sort=price&desc=true&page=2
    public async Task<IActionResult> Index(string? search, int? groupId, string? status,
                                           string? sort, bool desc = false, int page = 1)
    {
        var model = new ProductIndexViewModel
        {
            Search = search,
            GroupId = groupId,
            Status = ProductStatus.All.Contains(status ?? "") ? status : null,
            Sort = string.IsNullOrWhiteSpace(sort) ? "name" : sort,
            Desc = desc
        };

        IQueryable<Product> query = _db.Products.AsNoTracking().Where(p => !p.IsDeleted);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{EscapeLike(search.Trim())}%";
            query = query.Where(p => EF.Functions.ILike(p.ProductName, pattern)
                                  || EF.Functions.ILike(p.Sku, pattern));
        }

        if (groupId.HasValue)
        {
            query = query.Where(p => p.GroupId == groupId.Value);
        }

        if (model.Status != null)
        {
            query = query.Where(p => p.Status == model.Status);
        }

        IOrderedQueryable<Product> ordered = model.Sort switch
        {
            "sku"     => desc ? query.OrderByDescending(p => p.Sku)            : query.OrderBy(p => p.Sku),
            "group"   => desc ? query.OrderByDescending(p => p.Group.GroupName) : query.OrderBy(p => p.Group.GroupName),
            "price"   => desc ? query.OrderByDescending(p => p.Price)          : query.OrderBy(p => p.Price),
            "stock"   => desc ? query.OrderByDescending(p => p.StockQuantity)  : query.OrderBy(p => p.StockQuantity),
            "status"  => desc ? query.OrderByDescending(p => p.Status)         : query.OrderBy(p => p.Status),
            "updated" => desc ? query.OrderByDescending(p => p.UpdatedAt)      : query.OrderBy(p => p.UpdatedAt),
            _         => desc ? query.OrderByDescending(p => p.ProductName)   : query.OrderBy(p => p.ProductName)
        };
        ordered = ordered.ThenBy(p => p.ProductId);   // stable order between pages

        model.TotalCount = await ordered.CountAsync();
        model.Page = Math.Clamp(page, 1, model.TotalPages);

        model.Items = await ordered
            .Skip((model.Page - 1) * model.PageSize)
            .Take(model.PageSize)
            .Select(p => new ProductListItem
            {
                ProductId = p.ProductId,
                ProductName = p.ProductName,
                Sku = p.Sku,
                GroupName = p.Group.GroupName,
                Price = p.Price,
                StockQuantity = p.StockQuantity,
                Status = p.Status,
                UpdatedAt = p.UpdatedAt
            })
            .ToListAsync();

        await PopulateListsAsync(model.Groups, model.Statuses);
        return View(model);
    }

    // ---------- DETAILS ----------
    public async Task<IActionResult> Details(int id)
    {
        var model = await _db.Products
            .AsNoTracking()
            .Where(p => p.ProductId == id && !p.IsDeleted)
            .Select(p => new ProductDetailsViewModel
            {
                ProductId = p.ProductId,
                ProductName = p.ProductName,
                Sku = p.Sku,
                Description = p.Description,
                Price = p.Price,
                StockQuantity = p.StockQuantity,
                Status = p.Status,
                GroupName = p.Group.GroupName,
                CreatedAt = p.CreatedAt,
                UpdatedAt = p.UpdatedAt
            })
            .FirstOrDefaultAsync();

        if (model == null)
        {
            return NotFound();
        }

        // Change history written by the audit trigger (left join: changed_by can be empty)
        model.History = await (
            from l in _db.ProductAuditLogs.AsNoTracking()
            where l.ProductId == id
            join u in _db.AppUsers.AsNoTracking() on l.ChangedBy equals (int?)u.UserId into users
            from u in users.DefaultIfEmpty()
            orderby l.ChangedAt descending
            select new ProductHistoryItem
            {
                ChangedAt = l.ChangedAt,
                ChangedBy = u != null ? u.Username : null,
                OldPrice = l.OldPrice,
                NewPrice = l.NewPrice,
                OldStock = l.OldStock,
                NewStock = l.NewStock
            })
            .Take(10)
            .ToListAsync();

        return View(model);
    }

    // ---------- CREATE ----------
    [HttpGet]
    [Authorize(Roles = AppRoles.AdminOrEditor)]
    public async Task<IActionResult> Create()
    {
        var model = new ProductFormViewModel { Status = ProductStatus.Active };
        await PopulateListsAsync(model.Groups, model.Statuses);
        return View(model);
    }

    [HttpPost]
    [Authorize(Roles = AppRoles.AdminOrEditor)]
    public async Task<IActionResult> Create(ProductFormViewModel model)
    {
        var sku = NormalizeSku(model.Sku);
        await ValidateCommonAsync(model, sku, null);

        if (!ModelState.IsValid)
        {
            await PopulateListsAsync(model.Groups, model.Statuses);
            return View(model);
        }

        var product = new Product
        {
            ProductName = model.ProductName.Trim(),
            Description = CleanText(model.Description),
            Sku = sku,
            Price = model.Price!.Value,
            StockQuantity = model.StockQuantity,
            Status = model.Status,
            GroupId = model.GroupId!.Value
        };

        _db.Products.Add(product);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            ModelState.AddModelError(nameof(model.Sku), "This SKU is already used by another product.");
            await PopulateListsAsync(model.Groups, model.Statuses);
            return View(model);
        }

        TempData["Success"] = $"Product \"{product.ProductName}\" was created.";
        return RedirectToAction(nameof(Details), new { id = product.ProductId });
    }

    // ---------- EDIT ----------
    [HttpGet]
    [Authorize(Roles = AppRoles.AdminOrEditor)]
    public async Task<IActionResult> Edit(int id)
    {
        var product = await _db.Products.AsNoTracking()
            .FirstOrDefaultAsync(p => p.ProductId == id && !p.IsDeleted);
        if (product == null)
        {
            return NotFound();
        }

        var model = new ProductFormViewModel
        {
            ProductId = product.ProductId,
            IsEdit = true,
            ProductName = product.ProductName,
            Sku = product.Sku,
            Description = product.Description,
            Price = product.Price,
            CurrentStock = product.StockQuantity,
            Status = product.Status,
            GroupId = product.GroupId
        };

        await PopulateListsAsync(model.Groups, model.Statuses);
        return View(model);
    }

    [HttpPost]
    [Authorize(Roles = AppRoles.AdminOrEditor)]
    public async Task<IActionResult> Edit(int id, ProductFormViewModel model)
    {
        if (id != model.ProductId)
        {
            return BadRequest();
        }

        var product = await _db.Products.FirstOrDefaultAsync(p => p.ProductId == id && !p.IsDeleted);
        if (product == null)
        {
            return NotFound();
        }

        model.IsEdit = true;
        model.CurrentStock = product.StockQuantity;

        var sku = NormalizeSku(model.Sku);
        await ValidateCommonAsync(model, sku, id);

        if (!ModelState.IsValid)
        {
            await PopulateListsAsync(model.Groups, model.Statuses);
            return View(model);
        }

        product.ProductName = model.ProductName.Trim();
        product.Description = CleanText(model.Description);
        product.Sku = sku;
        product.Price = model.Price!.Value;     // a price change is logged by the audit trigger
        product.Status = model.Status;
        product.GroupId = model.GroupId!.Value;
        // stock is NOT changed here: use "Adjust stock" (stored procedure)
        // updated_at is set by the database trigger

        try
        {
            await SaveChangesWithAuditUserAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            ModelState.AddModelError(nameof(model.Sku), "This SKU is already used by another product.");
            await PopulateListsAsync(model.Groups, model.Statuses);
            return View(model);
        }

        TempData["Success"] = $"Product \"{product.ProductName}\" was updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // ---------- ADJUST STOCK (calls the stored procedure sp_adjust_stock) ----------
    [HttpGet]
    [Authorize(Roles = AppRoles.AdminOrEditor)]
    public async Task<IActionResult> AdjustStock(int id)
    {
        var model = await LoadAdjustStockAsync(id);
        if (model == null)
        {
            return NotFound();
        }

        return View(model);
    }

    [HttpPost]
    [Authorize(Roles = AppRoles.AdminOrEditor)]
    public async Task<IActionResult> AdjustStock(int id, AdjustStockViewModel input)
    {
        var model = await LoadAdjustStockAsync(id);
        if (model == null)
        {
            return NotFound();
        }

        model.Amount = input.Amount;

        if (input.Amount == 0)
        {
            ModelState.AddModelError(nameof(input.Amount), "The change amount must not be zero.");
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var amount = input.Amount!.Value;
        var userId = GetCurrentUserId();

        try
        {
            // The procedure locks the row, refuses negative stock and tells the trigger who made the change
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"CALL sp_adjust_stock({id}, {amount}, {userId})");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.RaiseException)
        {
            // Our own error message from RAISE EXCEPTION inside the procedure
            ModelState.AddModelError(nameof(input.Amount), ex.MessageText);
            return View(model);
        }

        TempData["Success"] = $"Stock of \"{model.ProductName}\" changed by {amount:+#;-#}.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // ---------- DELETE (soft delete) ----------
    [HttpGet]
    [Authorize(Roles = AppRoles.AdminOrEditor)]
    public async Task<IActionResult> Delete(int id)
    {
        var model = await _db.Products.AsNoTracking()
            .Where(p => p.ProductId == id && !p.IsDeleted)
            .Select(p => new ProductDeleteViewModel
            {
                ProductId = p.ProductId,
                ProductName = p.ProductName,
                Sku = p.Sku,
                GroupName = p.Group.GroupName
            })
            .FirstOrDefaultAsync();

        if (model == null)
        {
            return NotFound();
        }

        return View(model);
    }

    [HttpPost, ActionName("Delete")]
    [Authorize(Roles = AppRoles.AdminOrEditor)]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var product = await _db.Products.FirstOrDefaultAsync(p => p.ProductId == id && !p.IsDeleted);
        if (product == null)
        {
            return NotFound();
        }

        // Soft delete: the row stays in the database (order and audit history stay valid),
        // but it disappears from the screens and its SKU becomes free again.
        product.IsDeleted = true;
        await _db.SaveChangesAsync();

        TempData["Success"] = $"Product \"{product.ProductName}\" was deleted.";
        return RedirectToAction(nameof(Index));
    }

    // ---------- helpers ----------

    private async Task ValidateCommonAsync(ProductFormViewModel model, string sku, int? excludeId)
    {
        if (!ProductStatus.All.Contains(model.Status))
        {
            ModelState.AddModelError(nameof(model.Status), "Please choose a valid status.");
        }

        if (model.GroupId.HasValue &&
            !await _db.ProductGroups.AnyAsync(g => g.GroupId == model.GroupId.Value))
        {
            ModelState.AddModelError(nameof(model.GroupId), "The selected group does not exist.");
        }

        // SKU must be unique among products that are not deleted (same rule as the partial unique index)
        if (sku.Length > 0 &&
            await _db.Products.AnyAsync(p => !p.IsDeleted && p.Sku == sku &&
                                             (excludeId == null || p.ProductId != excludeId)))
        {
            ModelState.AddModelError(nameof(model.Sku), "This SKU is already used by another product.");
        }
    }

    private async Task<AdjustStockViewModel?> LoadAdjustStockAsync(int id)
    {
        return await _db.Products.AsNoTracking()
            .Where(p => p.ProductId == id && !p.IsDeleted)
            .Select(p => new AdjustStockViewModel
            {
                ProductId = p.ProductId,
                ProductName = p.ProductName,
                Sku = p.Sku,
                CurrentStock = p.StockQuantity
            })
            .FirstOrDefaultAsync();
    }

    private async Task PopulateListsAsync(List<SelectListItem> groups, List<SelectListItem> statuses)
    {
        var rows = await _db.ProductGroups.AsNoTracking()
            .OrderBy(g => g.GroupName)
            .Select(g => new { g.GroupId, g.GroupName })
            .ToListAsync();

        groups.Clear();
        groups.AddRange(rows.Select(g => new SelectListItem(g.GroupName, g.GroupId.ToString())));

        statuses.Clear();
        statuses.AddRange(ProductStatus.All.Select(s => new SelectListItem(s, s)));
    }

    // Saves the changes in one transaction and first tells PostgreSQL who is making them,
    // so the audit trigger can store the user id in product_audit_log.changed_by
    private async Task SaveChangesWithAuditUserAsync()
    {
        await using var tx = await _db.Database.BeginTransactionAsync();

        var userId = GetCurrentUserId();
        if (userId.HasValue)
        {
            var idText = userId.Value.ToString();
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('app.current_user_id', {idText}, true)");
        }

        await _db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    private int? GetCurrentUserId()
    {
        var value = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(value, out var id) ? id : null;
    }

    private static string NormalizeSku(string? sku)
    {
        return (sku ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static string? CleanText(string? text)
    {
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string EscapeLike(string input)
    {
        return input.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    }
}
