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
    private const int MinImageRows = 3;
    private const int MinAttributeRows = 5;
    private const int MaxImageRows = 20;
    private const int MaxAttributeRows = 30;
    private const int MaxTags = 10;

    private readonly AppDbContext _db;

    public ProductsController(AppDbContext db)
    {
        _db = db;
    }

    // ---------- LIST + SEARCH + FILTER + SORT + PAGING ----------
    // GET: /Products?search=dell&groupId=1&status=Active&tag=premium&sort=price&desc=true&page=2
    public async Task<IActionResult> Index(string? search, int? groupId, string? status, string? tag,
                                           string? sort, bool desc = false, int page = 1)
    {
        var model = new ProductIndexViewModel
        {
            Search = search,
            GroupId = groupId,
            Status = ProductStatus.All.Contains(status ?? "") ? status : null,
            Tag = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim(),
            Sort = string.IsNullOrWhiteSpace(sort) ? "name" : sort,
            Desc = desc
        };

        IQueryable<Product> query = _db.Products.AsNoTracking().Where(p => !p.IsDeleted);

        if (!string.IsNullOrWhiteSpace(search))
        {
            // Searches the name, the SKU and the attribute values (for example "16 GB")
            var pattern = $"%{EscapeLike(search.Trim())}%";
            query = query.Where(p => EF.Functions.ILike(p.ProductName, pattern)
                                  || EF.Functions.ILike(p.Sku, pattern)
                                  || p.ProductAttributes.Any(a => EF.Functions.ILike(a.AttributeValue, pattern)));
        }

        if (groupId.HasValue)
        {
            query = query.Where(p => p.GroupId == groupId.Value);
        }

        if (model.Status != null)
        {
            query = query.Where(p => p.Status == model.Status);
        }

        if (model.Tag != null)
        {
            // many-to-many filter through the product_tag table
            var tagName = model.Tag;
            query = query.Where(p => p.Tags.Any(t => t.TagName == tagName));
        }

        IOrderedQueryable<Product> ordered = model.Sort switch
        {
            "sku"     => desc ? query.OrderByDescending(p => p.Sku)               : query.OrderBy(p => p.Sku),
            "group"   => desc ? query.OrderByDescending(p => p.Group.GroupName)   : query.OrderBy(p => p.Group.GroupName),
            "price"   => desc ? query.OrderByDescending(p => p.Price)             : query.OrderBy(p => p.Price),
            "stock"   => desc ? query.OrderByDescending(p => p.StockQuantity)     : query.OrderBy(p => p.StockQuantity),
            "status"  => desc ? query.OrderByDescending(p => p.Status)            : query.OrderBy(p => p.Status),
            "updated" => desc ? query.OrderByDescending(p => p.UpdatedAt)         : query.OrderBy(p => p.UpdatedAt),
            _         => desc ? query.OrderByDescending(p => p.ProductName)       : query.OrderBy(p => p.ProductName)
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
                UpdatedAt = p.UpdatedAt,
                PrimaryImageUrl = p.ProductImages
                    .Where(i => i.IsPrimary)
                    .Select(i => i.ImageUrl)
                    .FirstOrDefault(),
                Tags = p.Tags.Select(t => t.TagName).OrderBy(n => n).ToList()
            })
            .ToListAsync();

        await PopulateGroupsAndStatusesAsync(model.Groups, model.Statuses);

        var tagNames = await LoadTagNamesAsync();
        model.Tags = tagNames.Select(n => new SelectListItem(n, n)).ToList();

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

        model.Images = await _db.ProductImages.AsNoTracking()
            .Where(i => i.ProductId == id)
            .OrderBy(i => i.DisplayOrder)
            .Select(i => new ProductImageItem { Url = i.ImageUrl, IsPrimary = i.IsPrimary })
            .ToListAsync();

        model.Attributes = await _db.ProductAttributes.AsNoTracking()
            .Where(a => a.ProductId == id)
            .OrderBy(a => a.AttributeName)
            .Select(a => new ProductAttributeItem { Name = a.AttributeName, Value = a.AttributeValue })
            .ToListAsync();

        model.Tags = await _db.Products.AsNoTracking()
            .Where(p => p.ProductId == id)
            .SelectMany(p => p.Tags)
            .Select(t => t.TagName)
            .OrderBy(n => n)
            .ToListAsync();

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
        PadRows(model);
        await PopulateFormListsAsync(model);
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
            PadRows(model);
            await PopulateFormListsAsync(model);
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

        AddImages(product, BuildImages(model));
        AddAttributes(product, BuildAttributes(model));
        await SyncTagsAsync(product, ParseTags(model.TagsText));

        // One SaveChanges = one database transaction: the product and all its images,
        // attributes and tag links are saved together, or nothing is saved.
        _db.Products.Add(product);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            AddUniqueError(pg, model);
            PadRows(model);
            await PopulateFormListsAsync(model);
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
            .Include(p => p.ProductImages)
            .Include(p => p.ProductAttributes)
            .Include(p => p.Tags)
            .FirstOrDefaultAsync(p => p.ProductId == id && !p.IsDeleted);

        if (product == null)
        {
            return NotFound();
        }

        var images = product.ProductImages.OrderBy(i => i.DisplayOrder).ToList();

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
            GroupId = product.GroupId,
            Images = images.Select(i => new ProductImageInput { ImageUrl = i.ImageUrl }).ToList(),
            PrimaryImageIndex = Math.Max(0, images.FindIndex(i => i.IsPrimary)),
            Attributes = product.ProductAttributes
                .OrderBy(a => a.AttributeName)
                .Select(a => new ProductAttributeInput { Name = a.AttributeName, Value = a.AttributeValue })
                .ToList(),
            TagsText = string.Join(", ", product.Tags.Select(t => t.TagName).OrderBy(n => n))
        };

        // two empty rows are shown after the existing ones, so new items can be added
        model.Images.AddRange(Enumerable.Range(0, 2).Select(_ => new ProductImageInput()));
        model.Attributes.AddRange(Enumerable.Range(0, 2).Select(_ => new ProductAttributeInput()));
        PadRows(model);

        await PopulateFormListsAsync(model);
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

        var product = await _db.Products
            .Include(p => p.ProductImages)
            .Include(p => p.ProductAttributes)
            .Include(p => p.Tags)
            .FirstOrDefaultAsync(p => p.ProductId == id && !p.IsDeleted);

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
            PadRows(model);
            await PopulateFormListsAsync(model);
            return View(model);
        }

        var images = BuildImages(model);
        var attributes = BuildAttributes(model);
        var tagNames = ParseTags(model.TagsText);

        try
        {
            await RunInTransactionAsync(async () =>
            {
                // Phase 1: update the product, remove the old images and attributes, fix the tag links
                product.ProductName = model.ProductName.Trim();
                product.Description = CleanText(model.Description);
                product.Sku = sku;
                product.Price = model.Price!.Value;     // a price change is logged by the audit trigger
                product.Status = model.Status;
                product.GroupId = model.GroupId!.Value;
                // stock is NOT changed here: use "Adjust stock" (stored procedure)

                _db.ProductImages.RemoveRange(product.ProductImages.ToList());
                _db.ProductAttributes.RemoveRange(product.ProductAttributes.ToList());
                await SyncTagsAsync(product, tagNames);
                await _db.SaveChangesAsync();

                // Phase 2: insert the new images and attributes
                // (two phases because "only one primary image" is a unique index that must not clash)
                AddImages(product, images);
                AddAttributes(product, attributes);
                await _db.SaveChangesAsync();
            });
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            AddUniqueError(pg, model);
            PadRows(model);
            await PopulateFormListsAsync(model);
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

        // Soft delete: the row stays in the database (history stays valid),
        // but it disappears from the screens and its SKU becomes free again.
        product.IsDeleted = true;
        await _db.SaveChangesAsync();

        TempData["Success"] = $"Product \"{product.ProductName}\" was deleted.";
        return RedirectToAction(nameof(Index));
    }

    // =====================================================================
    // helpers
    // =====================================================================

    // ---- validation ----
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

        // images
        if (model.Images.Count > MaxImageRows)
        {
            ModelState.AddModelError(string.Empty, "Too many image rows.");
        }
        else
        {
            for (var i = 0; i < model.Images.Count; i++)
            {
                var url = (model.Images[i].ImageUrl ?? string.Empty).Trim();
                if (url.Length == 0)
                {
                    continue;
                }

                var ok = Uri.TryCreate(url, UriKind.Absolute, out var uri)
                         && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
                if (!ok)
                {
                    ModelState.AddModelError($"Images[{i}].ImageUrl", "Enter a valid http or https address.");
                }
            }
        }

        // attributes
        if (model.Attributes.Count > MaxAttributeRows)
        {
            ModelState.AddModelError(string.Empty, "Too many attribute rows.");
        }
        else
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < model.Attributes.Count; i++)
            {
                var name = (model.Attributes[i].Name ?? string.Empty).Trim();
                var value = (model.Attributes[i].Value ?? string.Empty).Trim();

                if (name.Length == 0 && value.Length == 0)
                {
                    continue;
                }

                if (name.Length == 0)
                {
                    ModelState.AddModelError($"Attributes[{i}].Name", "Enter a name.");
                }
                else if (value.Length == 0)
                {
                    ModelState.AddModelError($"Attributes[{i}].Value", "Enter a value.");
                }
                else if (!seen.Add(name))
                {
                    ModelState.AddModelError($"Attributes[{i}].Name", "This attribute name is used twice.");
                }
            }
        }

        // tags
        var tags = ParseTags(model.TagsText);
        if (tags.Count > MaxTags)
        {
            ModelState.AddModelError(nameof(model.TagsText), $"Use at most {MaxTags} tags.");
        }
        else if (tags.Any(t => t.Length > 50))
        {
            ModelState.AddModelError(nameof(model.TagsText), "Each tag can be at most 50 characters.");
        }
    }

    private void AddUniqueError(PostgresException pg, ProductFormViewModel model)
    {
        if (pg.ConstraintName == "ux_product_sku_active")
        {
            ModelState.AddModelError(nameof(model.Sku), "This SKU is already used by another product.");
        }
        else
        {
            ModelState.AddModelError(string.Empty, "A value that must be unique already exists. Please check the form and try again.");
        }
    }

    // ---- turning form rows into clean data ----
    private static List<(string Url, bool IsPrimary)> BuildImages(ProductFormViewModel model)
    {
        var rows = model.Images
            .Select((x, i) => (Index: i, Url: (x.ImageUrl ?? string.Empty).Trim()))
            .Where(x => x.Url.Length > 0)
            .ToList();

        if (rows.Count == 0)
        {
            return new List<(string, bool)>();
        }

        // the chosen radio button, or the first filled row if the chosen row is empty
        var primary = rows.Any(r => r.Index == model.PrimaryImageIndex) ? model.PrimaryImageIndex : rows[0].Index;
        return rows.Select(r => (Url: r.Url, IsPrimary: r.Index == primary)).ToList();
    }

    private static List<(string Name, string Value)> BuildAttributes(ProductFormViewModel model)
    {
        return model.Attributes
            .Select(a => (Name: (a.Name ?? string.Empty).Trim(), Value: (a.Value ?? string.Empty).Trim()))
            .Where(a => a.Name.Length > 0 && a.Value.Length > 0)
            .ToList();
    }

    private static List<string> ParseTags(string? text)
    {
        return (text ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length > 0)
            .Distinct()
            .ToList();
    }

    private static void AddImages(Product product, List<(string Url, bool IsPrimary)> images)
    {
        var order = 0;
        foreach (var (url, isPrimary) in images)
        {
            product.ProductImages.Add(new ProductImage
            {
                ImageUrl = url,
                IsPrimary = isPrimary,
                DisplayOrder = order++
            });
        }
    }

    private static void AddAttributes(Product product, List<(string Name, string Value)> attributes)
    {
        foreach (var (name, value) in attributes)
        {
            product.ProductAttributes.Add(new ProductAttribute
            {
                AttributeName = name,
                AttributeValue = value
            });
        }
    }

    // Many-to-many: remove the links that are no longer wanted, add the missing ones.
    // Tags that do not exist yet are created.
    private async Task SyncTagsAsync(Product product, List<string> wantedNames)
    {
        foreach (var tag in product.Tags.Where(t => !wantedNames.Contains(t.TagName.ToLower())).ToList())
        {
            product.Tags.Remove(tag);
        }

        var have = product.Tags.Select(t => t.TagName.ToLower()).ToHashSet();
        var missing = wantedNames.Where(n => !have.Contains(n)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var existing = await _db.Tags.Where(t => missing.Contains(t.TagName.ToLower())).ToListAsync();

        foreach (var name in missing)
        {
            var tag = existing.FirstOrDefault(t => t.TagName.ToLower() == name) ?? new Tag { TagName = name };
            product.Tags.Add(tag);
        }
    }

    // ---- form helpers ----

    // Makes sure the form always shows some empty rows
    private static void PadRows(ProductFormViewModel model)
    {
        while (model.Images.Count < MinImageRows)
        {
            model.Images.Add(new ProductImageInput());
        }

        while (model.Attributes.Count < MinAttributeRows)
        {
            model.Attributes.Add(new ProductAttributeInput());
        }
    }

    private async Task PopulateFormListsAsync(ProductFormViewModel model)
    {
        await PopulateGroupsAndStatusesAsync(model.Groups, model.Statuses);
        model.ExistingTags = await LoadTagNamesAsync();
    }

    private async Task PopulateGroupsAndStatusesAsync(List<SelectListItem> groups, List<SelectListItem> statuses)
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

    private Task<List<string>> LoadTagNamesAsync()
    {
        return _db.Tags.AsNoTracking().OrderBy(t => t.TagName).Select(t => t.TagName).ToListAsync();
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

    // ---- transaction + audit user ----

    // Runs the work in ONE transaction and first tells PostgreSQL who is making the changes,
    // so the audit trigger can store the user id in product_audit_log.changed_by
    private async Task RunInTransactionAsync(Func<Task> work)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();

        var userId = GetCurrentUserId();
        if (userId.HasValue)
        {
            var idText = userId.Value.ToString();
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('app.current_user_id', {idText}, true)");
        }

        await work();
        await tx.CommitAsync();
    }

    private int? GetCurrentUserId()
    {
        var value = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(value, out var id) ? id : null;
    }

    // ---- small text helpers ----
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
