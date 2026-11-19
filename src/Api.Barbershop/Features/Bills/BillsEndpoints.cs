using System.Security.Claims;
using Api.Barbershop.Features.Auth;
using Barbershop.Application.Auth;
using Barbershop.Application.Bills;
using Barbershop.Application.Common.Paging;

namespace Api.Barbershop.Features.Bills;

/// <summary>
/// ADR-0020: bills. The group needs Admin or a permission that works with bills; each action checks its own permission
/// again against the database (open, close, charge, discount, manage). Approving and voiding are critical permissions and
/// have their own policies, verified in the database on every request (ADR-0005).
/// </summary>
public static class BillsEndpoints
{
    public static RouteGroupBuilder MapBillsEndpoints(this RouteGroupBuilder api)
    {
        var bills = api.MapGroup("/bills")
            .WithTags("Bills")
            .RequireAuthorization(AuthPolicyNames.BillsAccess);

        bills.MapGet("/options", (ClaimsPrincipal user, IBillsService service, CancellationToken ct)
                => service.GetOptionsAsync(user.GetRequiredUserId(), ct))
            .WithName("GetBillOptions")
            .Produces<BillOptionsView>(StatusCodes.Status200OK);

        bills.MapGet(string.Empty, ([AsParameters] BillListQuery query, ClaimsPrincipal user, IBillsService service, CancellationToken ct)
                => service.ListAsync(user.GetRequiredUserId(), query, ct))
            .WithName("GetBills")
            .Produces<PagedResult<BillSummaryView>>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        bills.MapGet("/pending", ([AsParameters] BillPendingQuery query, ClaimsPrincipal user, IBillsService service, CancellationToken ct)
                => service.GetPendingAsync(user.GetRequiredUserId(), query, ct))
            .WithName("GetPendingBills")
            .Produces<BillPendingView>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        bills.MapGet("/pending/count", (ClaimsPrincipal user, IBillsService service, CancellationToken ct)
                => service.GetPendingCountAsync(user.GetRequiredUserId(), ct))
            .WithName("GetPendingBillsCount")
            .Produces<BillCountView>(StatusCodes.Status200OK);

        bills.MapGet("/customers", (string? q, ClaimsPrincipal user, IBillsService service, CancellationToken ct)
                => service.SearchCustomersAsync(user.GetRequiredUserId(), q, ct))
            .WithName("SearchBillCustomers")
            .RequireRateLimiting(global::Api.Barbershop.Configuration.RateLimitPolicyNames.BillCustomerSearch)
            .Produces<IReadOnlyList<BillCustomerOption>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        bills.MapGet("/{billId:guid}", (Guid billId, ClaimsPrincipal user, IBillsService service, CancellationToken ct)
                => service.GetAsync(user.GetRequiredUserId(), billId, ct))
            .WithName("GetBill")
            .Produces<BillView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        bills.MapGet("/{billId:guid}/events", (Guid billId, ClaimsPrincipal user, IBillsService service, CancellationToken ct)
                => service.GetEventsAsync(user.GetRequiredUserId(), billId, ct))
            .WithName("GetBillEvents")
            .Produces<IReadOnlyList<BillEventView>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        bills.MapGet("/{billId:guid}/membership-options", (Guid billId, ClaimsPrincipal user, IBillsService service, CancellationToken ct)
                => service.GetMembershipOptionsAsync(user.GetRequiredUserId(), billId, ct))
            .WithName("GetBillMembershipOptions")
            .Produces<IReadOnlyList<BillMembershipOption>>(StatusCodes.Status200OK);

        bills.MapGet("/{billId:guid}/redemption-options", (Guid billId, ClaimsPrincipal user, IBillsService service, CancellationToken ct)
                => service.GetRedemptionOptionsAsync(user.GetRequiredUserId(), billId, ct))
            .WithName("GetBillRedemptionOptions")
            .Produces<IReadOnlyList<BillRedemptionOption>>(StatusCodes.Status200OK);

        // 200 rather than 201: a retried opening returns the bill already stored.
        bills.MapPost(string.Empty, (BillOpenRequest request, ClaimsPrincipal user, IBillsService service, CancellationToken ct)
                => service.OpenAsync(user.GetRequiredUserId(), request, ct))
            .WithName("OpenBill")
            .Produces<BillView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        bills.MapPut("/{billId:guid}", (Guid billId, BillHeaderRequest request, ClaimsPrincipal user, IBillsService service, CancellationToken ct)
                => service.UpdateHeaderAsync(user.GetRequiredUserId(), billId, request, ct))
            .WithName("UpdateBillHeader")
            .Produces<BillView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        bills.MapPost("/{billId:guid}/lines", (Guid billId, BillLineRequest request, ClaimsPrincipal user, IBillsService service, CancellationToken ct)
                => service.AddLineAsync(user.GetRequiredUserId(), billId, request, ct))
            .WithName("AddBillLine")
            .Produces<BillView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        bills.MapPut("/{billId:guid}/lines/{lineId:guid}",
                (Guid billId, Guid lineId, BillLineRequest request, ClaimsPrincipal user, IBillsService service, CancellationToken ct)
                    => service.UpdateLineAsync(user.GetRequiredUserId(), billId, lineId, request, ct))
            .WithName("UpdateBillLine")
            .Produces<BillView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        bills.MapDelete("/{billId:guid}/lines/{lineId:guid}",
                (Guid billId, Guid lineId, uint? version, ClaimsPrincipal user, IBillsService service, CancellationToken ct)
                    => service.RemoveLineAsync(user.GetRequiredUserId(), billId, lineId, version, ct))
            .WithName("RemoveBillLine")
            .Produces<BillView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        bills.MapPost("/{billId:guid}/close", (Guid billId, BillCloseRequest request, ClaimsPrincipal user, IBillsService service, CancellationToken ct)
                => service.CloseAsync(user.GetRequiredUserId(), billId, request, ct))
            .WithName("CloseBill")
            .Produces<BillView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        bills.MapPost("/{billId:guid}/detach-appointment",
                (Guid billId, BillReasonRequest request, ClaimsPrincipal user, IBillsService service, CancellationToken ct)
                    => service.DetachAppointmentAsync(user.GetRequiredUserId(), billId, request, ct))
            .WithName("DetachBillAppointment")
            .Produces<BillView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        bills.MapPost("/{billId:guid}/void", (Guid billId, BillReasonRequest request, ClaimsPrincipal user, IBillsService service, CancellationToken ct)
                => service.VoidAsync(user.GetRequiredUserId(), billId, request, ct))
            .RequireAuthorization(AuthPolicyNames.BillsVoidAccess)
            .WithName("VoidBill")
            .Produces<BillView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        bills.MapPut("/{billId:guid}/payment-method",
                (Guid billId, BillPaymentMethodRequest request, ClaimsPrincipal user, IBillsService service, CancellationToken ct)
                    => service.ChangePaymentMethodAsync(user.GetRequiredUserId(), billId, request, ct))
            .RequireAuthorization(AuthPolicyNames.BillsApproveAccess)
            .WithName("ChangeBillPaymentMethod")
            .Produces<BillView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        bills.MapGet("/{billId:guid}/approval-preview", (Guid billId, ClaimsPrincipal user, IBillApprovalService service, CancellationToken ct)
                => service.PreviewAsync(user.GetRequiredUserId(), billId, ct))
            .RequireAuthorization(AuthPolicyNames.BillsApproveAccess)
            .WithName("GetBillApprovalPreview")
            .Produces<BillApprovalPreviewView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        bills.MapPost("/{billId:guid}/approve",
                (Guid billId, BillApproveRequest request, ClaimsPrincipal user, IBillApprovalService service, CancellationToken ct)
                    => service.ApproveAsync(user.GetRequiredUserId(), billId, request, ct))
            .RequireAuthorization(AuthPolicyNames.BillsApproveAccess)
            .WithName("ApproveBill")
            .Produces<BillView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        bills.MapPost("/{billId:guid}/unapprove",
                (Guid billId, BillUnapproveRequest request, ClaimsPrincipal user, IBillApprovalService service, CancellationToken ct)
                    => service.UnapproveAsync(user.GetRequiredUserId(), billId, request, ct))
            .RequireAuthorization(AuthPolicyNames.BillsUnapproveAccess)
            .WithName("UnapproveBill")
            .Produces<BillView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        return api;
    }

    /// <summary>
    /// ADR-0020 stage 5 (P8, Q23): the customer's receipts, read only. The owner is always the signed-in user's linked
    /// profile, with the profiles merged into it.
    /// </summary>
    public static RouteGroupBuilder MapCustomerBillsEndpoints(this RouteGroupBuilder api)
    {
        var mine = api.MapGroup("/customer/bills")
            .WithTags("Customer")
            .RequireAuthorization(AuthPolicyNames.Customer);

        mine.MapGet(string.Empty, ([AsParameters] MyBillListQuery query, ClaimsPrincipal user, IMyBillsService service, CancellationToken ct)
                => service.GetMineAsync(user.GetRequiredUserId(), query, ct))
            .WithName("GetCustomerBills")
            .Produces<PagedResult<MyBillSummaryView>>(StatusCodes.Status200OK);

        mine.MapGet("/{billId:guid}", (Guid billId, ClaimsPrincipal user, IMyBillsService service, CancellationToken ct)
                => service.GetMineByIdAsync(user.GetRequiredUserId(), billId, ct))
            .WithName("GetCustomerBill")
            .Produces<MyBillView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // ADR-0020 stage 5: what was charged by day and payment method. ADR-0021 L2: its own permission, reports.cash.view
        // (critical: checked against the database on every request), held by Admin by default.
        api.MapGet("/admin/reports/cash", ([AsParameters] CashClosingQuery query, IBillReportsService service, CancellationToken ct)
                => service.GetCashClosingAsync(query, ct))
            .WithTags("Admin")
            .RequireAuthorization(AuthPolicyNames.CashClosingAccess)
            .WithName("GetAdminCashClosing")
            .Produces<CashClosingView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        return api;
    }

    /// <summary>Q1: the payment methods, kept by the administration; deactivated instead of deleted.</summary>
    public static RouteGroupBuilder MapAdminPaymentMethodsEndpoints(this RouteGroupBuilder api)
    {
        var methods = api.MapGroup("/admin/payment-methods")
            .WithTags("Admin")
            .RequireAuthorization(AuthPolicyNames.Admin);

        methods.MapGet(string.Empty, (IPaymentMethodsService service, CancellationToken ct) => service.ListAsync(ct))
            .WithName("GetAdminPaymentMethods")
            .Produces<IReadOnlyList<PaymentMethodView>>(StatusCodes.Status200OK);

        methods.MapPost(string.Empty, (PaymentMethodUpsertRequest request, IPaymentMethodsService service, CancellationToken ct)
                => service.CreateAsync(request, ct))
            .WithName("CreateAdminPaymentMethod")
            .Produces<PaymentMethodView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        methods.MapPut("/{paymentMethodId:guid}", (Guid paymentMethodId, PaymentMethodUpsertRequest request, IPaymentMethodsService service, CancellationToken ct)
                => service.UpdateAsync(paymentMethodId, request, ct))
            .WithName("UpdateAdminPaymentMethod")
            .Produces<PaymentMethodView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        return api;
    }
}
