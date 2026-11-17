using System.Security.Claims;
using Api.Barbershop.Features.Auth;
using Barbershop.Application.Auth;
using Barbershop.Application.Common.Paging;
using Barbershop.Application.Products;

namespace Api.Barbershop.Features.Admin.Products;

/// <summary>
/// Product inventory (ADR-0020 stage 1). Reading needs Admin or any inventory permission; receipts and the minimum stock
/// need inventory.receive; adjustments need inventory.adjust, which is critical (checked against the database).
/// </summary>
public static class AdminInventoryEndpoints
{
    public static RouteGroupBuilder MapAdminInventoryEndpoints(this RouteGroupBuilder api)
    {
        var inventory = api.MapGroup("/admin/inventory")
            .WithTags("Admin")
            .RequireAuthorization(AuthPolicyNames.InventoryAccess);

        inventory.MapGet(string.Empty, ([AsParameters] InventoryListQuery query, IAdminInventoryService service, CancellationToken ct)
                => service.ListAsync(query, ct))
            .WithName("GetAdminInventory")
            .Produces<PagedResult<InventoryItemView>>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        inventory.MapGet("/summary", (IAdminInventoryService service, CancellationToken ct) => service.GetSummaryAsync(ct))
            .WithName("GetAdminInventorySummary")
            .Produces<InventorySummaryView>(StatusCodes.Status200OK);

        inventory.MapGet("/sales", ([AsParameters] InventorySalesQuery query, IAdminInventoryService service, CancellationToken ct)
                => service.GetSalesAsync(query, ct))
            .WithName("GetAdminInventorySales")
            .Produces<InventorySalesView>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        inventory.MapGet("/{productId:guid}", (Guid productId, IAdminInventoryService service, CancellationToken ct)
                => service.GetAsync(productId, ct))
            .WithName("GetAdminInventoryItem")
            .Produces<InventoryItemView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        inventory.MapGet("/{productId:guid}/movements",
                (Guid productId, [AsParameters] InventoryMovementListQuery query, IAdminInventoryService service, CancellationToken ct)
                    => service.GetMovementsAsync(productId, query, ct))
            .WithName("GetAdminInventoryMovements")
            .Produces<PagedResult<InventoryMovementView>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        inventory.MapPost("/{productId:guid}/receipts",
                (Guid productId, InventoryReceiptRequest request, ClaimsPrincipal user, IAdminInventoryService service, CancellationToken ct)
                    => service.ReceiveAsync(user.GetRequiredUserId(), productId, request, ct))
            .RequireAuthorization(PermissionPolicyNames.InventoryReceive)
            .WithName("CreateAdminInventoryReceipt")
            .Produces<InventoryMovementResultView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        inventory.MapPost("/{productId:guid}/adjustments",
                (Guid productId, InventoryAdjustmentRequest request, ClaimsPrincipal user, IAdminInventoryService service, CancellationToken ct)
                    => service.AdjustAsync(user.GetRequiredUserId(), productId, request, ct))
            .RequireAuthorization(PermissionPolicyNames.InventoryAdjust)
            .WithName("CreateAdminInventoryAdjustment")
            .Produces<InventoryMovementResultView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        inventory.MapPut("/{productId:guid}/min-stock",
                (Guid productId, InventoryMinStockRequest request, IAdminInventoryService service, CancellationToken ct)
                    => service.SetMinStockAsync(productId, request, ct))
            .RequireAuthorization(PermissionPolicyNames.InventoryReceive)
            .WithName("UpdateAdminInventoryMinStock")
            .Produces<InventoryItemView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        return api;
    }
}
