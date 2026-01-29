using Microsoft.OpenApi;
using RoomBookingApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Room Booking API", Version = "v1" });
});

builder.Services.AddSingleton<IBookingRepository, InMemoryBookingRepository>();
builder.Services.AddSingleton<BookingService>();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

// Luo varaus
app.MapPost("/bookings", (RoomBookingApi.CreateBookingDto dto, BookingService service) =>
{
    try
    {
        var booking = service.CreateBooking(new BookingRequest
        {
            RoomId = dto.RoomId,
            ReservedBy = dto.ReservedBy,
            Start = dto.Start,
            End = dto.End
        });

        return Results.Created($"/bookings/{booking.Id}", Mapper.ToDto(booking));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
});

// Peruuta varaus
app.MapDelete("/bookings/{id:guid}", (Guid id, BookingService service) =>
{
    var ok = service.CancelBooking(id);
    return ok ? Results.NoContent() : Results.NotFound(new { error = "Varausta ei löytynyt." });
});

// Listaa huoneen varaukset
app.MapGet("/rooms/{roomId}/bookings", (string roomId, BookingService service) =>
{
    try
    {
        var bookings = service.GetBookingsForRoom(roomId).Select(Mapper.ToDto);
        return Results.Ok(bookings);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.Run();


// ==================================================
// KAIKKI tyypit vasta tämän jälkeen (ei CS8803)
// ==================================================
namespace RoomBookingApi
{
    using System.Collections.Concurrent;

    // DTO:t
    public record CreateBookingDto(string RoomId, string ReservedBy, DateTimeOffset Start, DateTimeOffset End);
    public record BookingDto(Guid Id, string RoomId, string ReservedBy, DateTimeOffset Start, DateTimeOffset End);

    public static class Mapper
    {
        public static BookingDto ToDto(Booking b)
            => new(b.Id, b.RoomId, b.ReservedBy, b.Start, b.End);
    }

    // Domain
    public record Booking(Guid Id, string RoomId, string ReservedBy, DateTimeOffset Start, DateTimeOffset End);

    public class BookingRequest
    {
        public required string RoomId { get; init; }
        public required string ReservedBy { get; init; }
        public required DateTimeOffset Start { get; init; }
        public required DateTimeOffset End { get; init; }
    }

    public interface IBookingRepository
    {
        Booking Add(Booking booking);
        bool Remove(Guid bookingId);
        IReadOnlyList<Booking> GetByRoom(string roomId);
    }

    public class InMemoryBookingRepository : IBookingRepository
    {
        private readonly ConcurrentDictionary<Guid, Booking> _bookings = new();

        public Booking Add(Booking booking)
        {
            if (!_bookings.TryAdd(booking.Id, booking))
                throw new InvalidOperationException("Booking with same ID already exists.");
            return booking;
        }

        public bool Remove(Guid bookingId) => _bookings.TryRemove(bookingId, out _);

        public IReadOnlyList<Booking> GetByRoom(string roomId)
            => _bookings.Values
                .Where(b => string.Equals(b.RoomId, roomId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(b => b.Start)
                .ToList();
    }

    public class BookingService
    {
        private readonly IBookingRepository _repo;
        private readonly ConcurrentDictionary<string, object> _roomLocks = new(StringComparer.OrdinalIgnoreCase);

        public BookingService(IBookingRepository repo) => _repo = repo;

        public Booking CreateBooking(BookingRequest request, DateTimeOffset? now = null)
        {
            var current = now ?? DateTimeOffset.UtcNow;
            ValidateRequest(request, current);

            var roomLock = _roomLocks.GetOrAdd(request.RoomId, _ => new object());

            lock (roomLock)
            {
                var existing = _repo.GetByRoom(request.RoomId);

                // Päällekkäisyys [Start, End): start < existingEnd && end > existingStart
                var overlaps = existing.Any(b => request.Start < b.End && request.End > b.Start);
                if (overlaps)
                    throw new InvalidOperationException("Huone on jo varattu kyseiselle aikavälille.");

                var booking = new Booking(Guid.NewGuid(), request.RoomId, request.ReservedBy, request.Start, request.End);
                return _repo.Add(booking);
            }
        }

        public bool CancelBooking(Guid bookingId) => _repo.Remove(bookingId);

        public IReadOnlyList<Booking> GetBookingsForRoom(string roomId)
        {
            if (string.IsNullOrWhiteSpace(roomId))
                throw new ArgumentException("RoomId puuttuu.", nameof(roomId));

            return _repo.GetByRoom(roomId);
        }

        private static void ValidateRequest(BookingRequest request, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(request.RoomId))
                throw new ArgumentException("RoomId puuttuu.");

            if (string.IsNullOrWhiteSpace(request.ReservedBy))
                throw new ArgumentException("ReservedBy puuttuu.");

            if (request.Start >= request.End)
                throw new InvalidOperationException("Aloitusajan täytyy olla ennen lopetusaikaa.");

            if (request.Start < now)
                throw new InvalidOperationException("Varaus ei voi alkaa menneisyydessä.");
        }
    }
}
