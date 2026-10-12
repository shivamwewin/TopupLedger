using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MediatR;
using TopupLedger.Application.Persistence;
using TopupLedger.Application.Portal;
using TopupLedger.Application.Topups;
using TopupLedger.Domain.Money;
using TopupLedger.Domain.Partners;
using TopupLedger.Infrastructure.Reconciliation;
using TopupLedger.Infrastructure.Security;

namespace TopupLedger.Portal.Controllers;

[Authorize]
public sealed class DashboardController : Controller
{
    private readonly IMediator _mediator;

    public DashboardController(IMediator mediator) => _mediator = mediator;

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var model = await _mediator.Send(new GetDashboardQuery(), cancellationToken);
        return View(model);
    }
}

[AllowAnonymous]
public sealed class AccountController : Controller
{
    private readonly ILedgerDbContext _db;

    public AccountController(ILedgerDbContext db) => _db = db;

    [HttpGet("/account/login")]
    public IActionResult Login() => View();

    [HttpPost("/account/login")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string username, string password, CancellationToken cancellationToken)
    {
        var user = await _db.PortalUsers.AsNoTracking().SingleOrDefaultAsync(u => u.Username == username, cancellationToken);
        if (user is null || !PortalPassword.Verify(password, user.PasswordSalt, user.PasswordHash))
        {
            ModelState.AddModelError(string.Empty, "Invalid username or password.");
            return View();
        }

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role)
        }, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return RedirectToAction("Index", "Dashboard");
    }

    [Authorize]
    [HttpPost("/account/logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync();
        return RedirectToAction(nameof(Login));
    }

    [HttpGet("/account/denied")]
    public IActionResult Denied() => View();
}

[Authorize]
public sealed class PartnersController : Controller
{
    private readonly IMediator _mediator;

    public PartnersController(IMediator mediator) => _mediator = mediator;

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var partners = await _mediator.Send(new ListPartnersQuery(), cancellationToken);
        return View(partners);
    }

    [Authorize(Roles = "ADMIN")]
    [HttpGet]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        ViewBag.Distributors = await Distributors(cancellationToken);
        return View();
    }

    [Authorize(Roles = "ADMIN")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string name, string tier, Guid? parentPartnerId, CancellationToken cancellationToken)
    {
        if (!PartnerLabels.TryParseTier(tier, out var parsed))
        {
            ModelState.AddModelError(string.Empty, "Tier must be RETAILER or DISTRIBUTOR.");
            ViewBag.Distributors = await Distributors(cancellationToken);
            return View();
        }

        try
        {
            var created = await _mediator.Send(new CreatePartnerCommand(name, parsed, parentPartnerId), cancellationToken);
            TempData["Secret"] = created.SecretPlaintext;
            TempData["ApiKey"] = created.ApiKey;
            TempData["Name"] = name;
            return RedirectToAction(nameof(Secret));
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            ViewBag.Distributors = await Distributors(cancellationToken);
            return View();
        }
    }

    [Authorize(Roles = "ADMIN")]
    public IActionResult Secret()
    {
        if (TempData["Secret"] is not string secret)
        {
            return RedirectToAction(nameof(Index));
        }

        ViewBag.Secret = secret;
        ViewBag.ApiKey = TempData["ApiKey"];
        ViewBag.Name = TempData["Name"];
        return View();
    }

    [Authorize(Roles = "ADMIN")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetStatus(Guid id, string status, CancellationToken cancellationToken)
    {
        var parsed = status == "SUSPENDED" ? PartnerStatus.Suspended : PartnerStatus.Active;
        await _mediator.Send(new SetPartnerStatusCommand(id, parsed), cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Roles = "ADMIN")]
    [HttpGet]
    public IActionResult Credit(Guid id) => View(id);

    [Authorize(Roles = "ADMIN")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Credit(Guid id, string amount, string reason, CancellationToken cancellationToken)
    {
        if (!Fils.TryParseAed(amount, out var fils, out var error))
        {
            ModelState.AddModelError(string.Empty, error ?? "Amount is invalid.");
            return View(id);
        }

        try
        {
            await _mediator.Send(new ManualCreditCommand(id, fils, reason, User.Identity?.Name), cancellationToken);
            return RedirectToAction(nameof(Index));
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return View(id);
        }
    }

    private async Task<IReadOnlyList<PartnerListItem>> Distributors(CancellationToken cancellationToken)
    {
        var all = await _mediator.Send(new ListPartnersQuery(), cancellationToken);
        return all.Where(p => p.Tier == "DISTRIBUTOR").ToList();
    }
}

[Authorize]
public sealed class TransactionsController : Controller
{
    private readonly IMediator _mediator;

    public TransactionsController(IMediator mediator) => _mediator = mediator;

    public async Task<IActionResult> Index(string? msisdn, string? clientRef, string? status, string? from, string? to, string? cursor, CancellationToken cancellationToken)
    {
        DateTime? fromUtc = Parse(from, false);
        DateTime? toUtc = Parse(to, true);
        var page = await _mediator.Send(new TransactionSearchQuery(msisdn, clientRef, status, fromUtc, toUtc, cursor), cancellationToken);
        ViewBag.Msisdn = msisdn;
        ViewBag.ClientRef = clientRef;
        ViewBag.Status = status;
        ViewBag.From = from;
        ViewBag.To = to;
        return View(page);
    }

    public async Task<IActionResult> Detail(Guid id, CancellationToken cancellationToken)
    {
        var model = await _mediator.Send(new GetTopupDetailQuery(id), cancellationToken);
        return model is null ? NotFound() : View(model);
    }

    private static DateTime? Parse(string? text, bool exclusiveDate)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            var day = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            return exclusiveDate ? day.AddDays(1) : day;
        }

        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var instant)
            ? instant
            : null;
    }
}

[Authorize]
public sealed class ReconciliationController : Controller
{
    private readonly IReconciliationImporter _importer;
    private readonly ILedgerDbContext _db;

    public ReconciliationController(IReconciliationImporter importer, ILedgerDbContext db)
    {
        _importer = importer;
        _db = db;
    }

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var runs = await _db.ReconciliationRuns.AsNoTracking().OrderByDescending(r => r.CreatedAtUtc).Take(20).ToListAsync(cancellationToken);
        return View(runs);
    }

    [Authorize(Roles = "ADMIN")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(40 * 1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            TempData["Error"] = "Choose a CSV file.";
            return RedirectToAction(nameof(Index));
        }

        await using var stream = file.OpenReadStream();
        var id = await _importer.ImportAsync(stream, file.FileName, cancellationToken);
        return RedirectToAction(nameof(Detail), new { id });
    }

    public async Task<IActionResult> Detail(Guid id, CancellationToken cancellationToken)
    {
        var run = await _db.ReconciliationRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (run is null)
        {
            return NotFound();
        }

        var findings = await _db.ReconciliationFindings.AsNoTracking()
            .Where(f => f.RunId == id)
            .OrderBy(f => f.Id)
            .Take(200)
            .ToListAsync(cancellationToken);
        ViewBag.Findings = findings;
        return View(run);
    }
}

[Authorize]
public sealed class SettlementController : Controller
{
    private readonly IMediator _mediator;

    public SettlementController(IMediator mediator) => _mediator = mediator;

    public async Task<IActionResult> Index(string? businessDay, CancellationToken cancellationToken)
    {
        var day = DateOnly.TryParse(businessDay, out var parsed) ? parsed : DateOnly.FromDateTime(DateTime.UtcNow);
        var progress = await _mediator.Send(new GetSettlementProgressQuery(day), cancellationToken);
        ViewBag.BusinessDay = day.ToString("yyyy-MM-dd");
        return View(progress);
    }

    [Authorize(Roles = "ADMIN")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Generate(string businessDay, CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParseExact(businessDay, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            TempData["Error"] = "Use YYYY-MM-DD.";
            return RedirectToAction(nameof(Index));
        }

        await _mediator.Send(new EnqueueSettlementCommand(day), cancellationToken);
        return RedirectToAction(nameof(Index), new { businessDay = day.ToString("yyyy-MM-dd") });
    }
}
