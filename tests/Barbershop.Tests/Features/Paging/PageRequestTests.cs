using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Common.Paging;
using Barbershop.Domain.Appointments;

namespace Barbershop.Tests.Features.Paging;

/// <summary>The shared page and filter validation of the paginated lists (ADR-0016 B7).</summary>
public sealed class PageRequestTests
{
  [Fact]
  public void MissingValues_TakeTheDefaults()
  {
    var page = PageRequest.Create(null, null);

    Assert.Equal((1, PageRequest.DefaultPageSize, 0), (page.Page, page.PageSize, page.Skip));
    Assert.Equal(25, PageRequest.DefaultPageSize);
    Assert.Equal(100, PageRequest.MaxPageSize);
  }

  [Theory]
  [InlineData(1, 1, 0)]
  [InlineData(3, 25, 50)]
  [InlineData(2, 100, 100)]
  public void ValidValues_ComputeTheRowsToSkip(int pageNumber, int pageSize, int skip)
  {
    var page = PageRequest.Create(pageNumber, pageSize);

    Assert.Equal((pageNumber, pageSize, skip), (page.Page, page.PageSize, page.Skip));
  }

  [Theory]
  [InlineData(0, null, "page")]
  [InlineData(-1, null, "page")]
  [InlineData(null, 0, "pageSize")]
  [InlineData(null, 101, "pageSize")]
  public void OutOfRangeValues_AreAValidationError(int? pageNumber, int? pageSize, string field)
  {
    var error = Assert.Throws<ValidationProblemException>(() => PageRequest.Create(pageNumber, pageSize));

    Assert.Equal([field], error.Errors.Keys);
  }

  [Fact]
  public void BothInvalid_ReportsBothFields()
  {
    var error = Assert.Throws<ValidationProblemException>(() => PageRequest.Create(0, 500));

    Assert.Equal(["page", "pageSize"], error.Errors.Keys.Order());
  }

  [Fact]
  public void Ranges_RejectAStartAfterTheEnd()
  {
    var day = new DateOnly(2026, 11, 6);
    ListFilters.ValidateRange(day, day);
    ListFilters.ValidateRange(null, day);
    ListFilters.ValidateRange(day, null);

    var error = Assert.Throws<ValidationProblemException>(() => ListFilters.ValidateRange(day, day.AddDays(-1), "soldFrom", "soldTo"));
    Assert.Equal(["soldFrom"], error.Errors.Keys);
  }

  [Fact]
  public void EnumFilters_AcceptNamesInAnyCasing_ButNeverNumbers()
  {
    Assert.Null(ListFilters.ParseEnum<AppointmentStatus>("  ", "status"));
    Assert.Equal(AppointmentStatus.NoShow, ListFilters.ParseEnum<AppointmentStatus>("noshow", "status"));
    Assert.Throws<ValidationProblemException>(() => ListFilters.ParseEnum<AppointmentStatus>("1", "status"));

    Assert.Null(ListFilters.ParseEnumList<AppointmentStatus>(null, "status"));
    Assert.Equal(
        [AppointmentStatus.Pending, AppointmentStatus.Confirmed],
        ListFilters.ParseEnumList<AppointmentStatus>("Pending, confirmed,Pending", "status")!);
    var error = Assert.Throws<ValidationProblemException>(() => ListFilters.ParseEnumList<AppointmentStatus>("Pending,Gone", "status"));
    Assert.Equal(["status"], error.Errors.Keys);
  }

  [Fact]
  public void CatalogStatus_IsActiveInactiveOrBoth()
  {
    Assert.Null(CatalogStatusFilter.Parse(null));
    Assert.True(CatalogStatusFilter.Parse("active"));
    Assert.False(CatalogStatusFilter.Parse("Inactive"));
    Assert.Throws<ValidationProblemException>(() => CatalogStatusFilter.Parse("Deleted"));
  }
}
