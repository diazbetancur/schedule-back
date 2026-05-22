namespace Barbershop.Application.Reviews;

public sealed record ReviewCreateRequest(
    int Stars,
    string? Comment);

public sealed record CustomerReviewView(
    Guid Id,
    Guid AppointmentId,
    Guid StaffProfileId,
    int Stars,
    string? Comment,
    DateTime CreatedAtUtc);

public sealed record PublicStaffReviewView(
    Guid AppointmentId,
    int Stars,
    string? Comment,
    DateTime CreatedAtUtc);

public sealed record PublicStaffReviewsSummaryView(
    Guid StaffProfileId,
    int TotalReviews,
    decimal AverageStars);