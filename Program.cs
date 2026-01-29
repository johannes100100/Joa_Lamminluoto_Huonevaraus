using Microsoft.OpenApi;
using RoomBookingApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Room Booking API",
        Version = "v1"
    });
});

builder.Services.AddSingleton<IBookingRepository, InMemoryBookingRepository>();
builder.Services.AddSingleton<BookingService>();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

// =======================
// ENDPOINTIT
// =======================

// Luo varaus
app.MapPost("/bookings", (CreateBookingDto dto, BookingService service) =>
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
    catch (BookingValidationException ex)
    {
        return Results.BadRequest(new { errors = ex.Errors });
    }
    catch (BookingConflictException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
});

// Peruuta varaus
app.MapDelete("/bookings/{id:guid}", (Guid id, BookingService service) =>
{
    var ok = service.CancelBooking(id);
    return ok ? Results.NoContent()
              : Results.NotFound(new { error = "Varausta ei löytynyt." });
});

// Listaa huoneen varaukset
app.MapGet("/rooms/{roomId}/bookings", (string roomId, BookingService service) =>
{
    try
    {
        var bookings = service.GetBookingsForRoom(roomId)
                              .Select(Mapper.ToDto);
        return Results.Ok(bookings);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Vapaat ajat
app.MapGet("/rooms/{roomId}/free-slots", (
    string roomId,
    DateTimeOffset start,
    DateTimeOffset end,
    double minHours,
    BookingService service) =>
{
    try
    {
        var slots = service.GetFreeSlots(
            roomId,
            start,
            end,
            TimeSpan.FromHours(minHours));

        return Results.Ok(slots);
    }
    catch (BookingValidationException ex)
    {
        return Results.BadRequest(new { errors = ex.Errors });
    }
});

app.Run();


// ==================================================
// DOMAIN + LOGIIKKA
// ==================================================
namespace RoomBookingApi
{
    using System.Collections.Concurrent;

    // DTO:t
    public record CreateBookingDto(string RoomId, string ReservedBy, DateTimeOffset Start, DateTimeOffset End);
    public record BookingDto(Guid Id, string RoomId, string ReservedBy, DateTimeOffset Start, DateTimeOffset End);
    public record FreeSlotDto(DateTimeOffset Start, DateTimeOffset End, double DurationHours);

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

    // Poikkeukset
    public class BookingValidationException : Exception
    {
        public IReadOnlyList<string> Errors { get; }

        public BookingValidationException(IEnumerable<string> errors)
        {
            Errors = errors.ToList();
        }
    }

    public class BookingConflictException : Exception
    {
        public BookingConflictException(string message) : base(message) { }
    }

    // Repository
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
            _bookings.TryAdd(booking.Id, booking);
            return booking;
        }

        public bool Remove(Guid bookingId)
            => _bookings.TryRemove(bookingId, out _);

        public IReadOnlyList<Booking> GetByRoom(string roomId)
            => _bookings.Values
                .Where(b => b.RoomId.Equals(roomId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(b => b.Start)
                .ToList();
    }

    // Service
    public class BookingService
    {
        private readonly IBookingRepository _repo;
        private readonly ConcurrentDictionary<string, object> _locks = new();

        public BookingService(IBookingRepository repo)
        {
            _repo = repo;
        }

        public Booking CreateBooking(BookingRequest request, DateTimeOffset? now = null)
        {
            var current = now ?? DateTimeOffset.UtcNow;
            ValidateRequest(request, current);

            var roomLock = _locks.GetOrAdd(request.RoomId, _ => new object());

            lock (roomLock)
            {
                var overlaps = _repo.GetByRoom(request.RoomId)
                    .Any(b => request.Start < b.End && request.End > b.Start);

                if (overlaps)
                    throw new BookingConflictException("Huone on jo varattu kyseiselle aikavälille.");

                var booking = new Booking(
                    Guid.NewGuid(),
                    request.RoomId,
                    request.ReservedBy,
                    request.Start,
                    request.End);

                return _repo.Add(booking);
            }
        }

        public bool CancelBooking(Guid id) => _repo.Remove(id);

        public IReadOnlyList<Booking> GetBookingsForRoom(string roomId)
        {
            if (string.IsNullOrWhiteSpace(roomId))
                throw new ArgumentException("RoomId puuttuu.");

            return _repo.GetByRoom(roomId);
        }

        // ===== VAPAAT AJAT =====
        public IReadOnlyList<FreeSlotDto> GetFreeSlots(
            string roomId,
            DateTimeOffset rangeStart,
            DateTimeOffset rangeEnd,
            TimeSpan minDuration)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(roomId))
                errors.Add("RoomId puuttuu.");

            if (rangeStart >= rangeEnd)
                errors.Add("Aikavälin alku täytyy olla ennen loppua.");

            if (minDuration <= TimeSpan.Zero)
                errors.Add("Minimikeston täytyy olla suurempi kuin 0.");

            if (errors.Any())
                throw new BookingValidationException(errors);

            var bookings = _repo.GetByRoom(roomId)
                .Where(b => b.Start < rangeEnd && b.End > rangeStart)
                .Select(b => (
                    Start: Max(b.Start, rangeStart),
                    End: Min(b.End, rangeEnd)))
                .OrderBy(b => b.Start)
                .ToList();

            var merged = new List<(DateTimeOffset Start, DateTimeOffset End)>();
            foreach (var b in bookings)
            {
                if (merged.Count == 0 || b.Start > merged[^1].End)
                    merged.Add(b);
                else
                    merged[^1] = (merged[^1].Start, Max(merged[^1].End, b.End));
            }

            var freeSlots = new List<FreeSlotDto>();
            var cursor = rangeStart;

            foreach (var busy in merged)
            {
                if (cursor < busy.Start)
                {
                    var dur = busy.Start - cursor;
                    if (dur >= minDuration)
                        freeSlots.Add(new FreeSlotDto(cursor, busy.Start, dur.TotalHours));
                }
                cursor = Max(cursor, busy.End);
            }

            if (cursor < rangeEnd)
            {
                var dur = rangeEnd - cursor;
                if (dur >= minDuration)
                    freeSlots.Add(new FreeSlotDto(cursor, rangeEnd, dur.TotalHours));
            }

            return freeSlots;
        }

        private static void ValidateRequest(BookingRequest request, DateTimeOffset now)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(request.RoomId))
                errors.Add("RoomId puuttuu.");

            if (string.IsNullOrWhiteSpace(request.ReservedBy))
                errors.Add("ReservedBy puuttuu.");

            if (request.Start >= request.End)
                errors.Add("Aloitusajan täytyy olla ennen lopetusaikaa.");

            if (request.Start < now)
                errors.Add("Varaus ei voi alkaa menneisyydessä.");

            if (errors.Any())
                throw new BookingValidationException(errors);
        }

        private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b)
            => a > b ? a : b;

        private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b)
            => a < b ? a : b;
    }
}
