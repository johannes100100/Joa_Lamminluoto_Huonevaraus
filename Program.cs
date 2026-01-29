using Microsoft.OpenApi;
using RoomBookingApi;

var builder = WebApplication.CreateBuilder(args);

// Swagger / OpenAPI (for easy testing in browser)
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Room Booking API",
        Version = "v1"
    });
});

// In-memory persistence + business logic layer
builder.Services.AddSingleton<IBookingRepository, InMemoryBookingRepository>();
builder.Services.AddSingleton<BookingService>();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();


// ==================================================
// API ENDPOINTS
// ==================================================

/*
 * POST /bookings
 * Creates a new booking (if valid and no overlaps exist).
 * Returns:
 *  - 201 Created + booking DTO on success
 *  - 400 BadRequest + { errors: [...] } for validation failures
 *  - 409 Conflict + { error: "..." } if time overlaps with an existing booking
 */
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

/*
 * DELETE /bookings/{id}
 * Cancels (deletes) a booking by its ID.
 * Returns:
 *  - 204 NoContent on success
 *  - 404 NotFound if booking ID does not exist
 */
app.MapDelete("/bookings/{id:guid}", (Guid id, BookingService service) =>
{
    var removed = service.CancelBooking(id);

    return removed
        ? Results.NoContent()
        : Results.NotFound(new { error = "Varausta ei löytynyt." });
});

/*
 * GET /rooms/{roomId}/bookings
 * Lists all bookings for a given room (sorted by start time).
 * Returns:
 *  - 200 OK + list of booking DTOs
 *  - 400 BadRequest if roomId is missing/invalid
 */
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

/*
 * GET /rooms/{roomId}/free-slots?start=...&end=...&minHours=...
 * Returns ALL free time windows inside the given range (24/7),
 * where each window is at least minHours long.
 *
 * Example:
 *  /rooms/A101/free-slots?start=2027-05-06T00:00:00%2B02:00&end=2027-05-08T00:00:00%2B02:00&minHours=2
 *
 * Returns:
 *  - 200 OK + list of free slot DTOs
 *  - 400 BadRequest + { errors: [...] } if parameters are invalid
 */
app.MapGet("/rooms/{roomId}/free-slots", (
    string roomId,
    DateTimeOffset start,
    DateTimeOffset end,
    double minHours,
    BookingService service) =>
{
    try
    {
        var minDuration = TimeSpan.FromHours(minHours);

        var slots = service.GetFreeSlots(
            roomId: roomId,
            rangeStart: start,
            rangeEnd: end,
            minDuration: minDuration);

        return Results.Ok(slots);
    }
    catch (BookingValidationException ex)
    {
        return Results.BadRequest(new { errors = ex.Errors });
    }
});

app.Run();


// ==================================================
// DOMAIN + BUSINESS LOGIC + IN-MEMORY STORAGE
// (Keeping everything in one file for simplicity)
// ==================================================
namespace RoomBookingApi
{
    using System.Collections.Concurrent;

    // -----------------------
    // DTOs (API Contracts)
    // -----------------------

    // Incoming booking creation request (from client)
    public record CreateBookingDto(string RoomId, string ReservedBy, DateTimeOffset Start, DateTimeOffset End);

    // Booking returned to clients
    public record BookingDto(Guid Id, string RoomId, string ReservedBy, DateTimeOffset Start, DateTimeOffset End);

    // Free slot returned to clients
    public record FreeSlotDto(DateTimeOffset Start, DateTimeOffset End, double DurationHours);

    // Maps internal domain models -> API DTOs
    public static class Mapper
    {
        public static BookingDto ToDto(Booking b)
            => new(b.Id, b.RoomId, b.ReservedBy, b.Start, b.End);
    }

    // -----------------------
    // Domain Model
    // -----------------------

    // Internal booking representation stored in the repository
    public record Booking(Guid Id, string RoomId, string ReservedBy, DateTimeOffset Start, DateTimeOffset End);

    // Internal request object used by service layer
    public class BookingRequest
    {
        public required string RoomId { get; init; }
        public required string ReservedBy { get; init; }
        public required DateTimeOffset Start { get; init; }
        public required DateTimeOffset End { get; init; }
    }

    // -----------------------
    // Exceptions
    // -----------------------

    /*
     * Validation exception carries multiple errors.
     * We use this for HTTP 400 BadRequest with { errors: [...] }.
     */
    public class BookingValidationException : Exception
    {
        public IReadOnlyList<string> Errors { get; }

        public BookingValidationException(IEnumerable<string> errors)
        {
            Errors = errors.ToList();
        }
    }

    /*
     * Conflict exception is used for overlaps.
     * We use this for HTTP 409 Conflict with { error: "..." }.
     */
    public class BookingConflictException : Exception
    {
        public BookingConflictException(string message) : base(message) { }
    }

    // -----------------------
    // Repository (In-memory "database")
    // -----------------------

    public interface IBookingRepository
    {
        Booking Add(Booking booking);
        bool Remove(Guid bookingId);
        IReadOnlyList<Booking> GetByRoom(string roomId);
    }

    /*
     * In-memory repository backed by ConcurrentDictionary.
     * Note: This is NOT persistent storage. All bookings are lost when the app stops.
     */
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

    // -----------------------
    // Service (Business logic)
    // -----------------------

    public class BookingService
    {
        private readonly IBookingRepository _repo;

        // One lock object per room to make "check overlaps + add" atomic
        private readonly ConcurrentDictionary<string, object> _roomLocks = new(StringComparer.OrdinalIgnoreCase);

        public BookingService(IBookingRepository repo)
        {
            _repo = repo;
        }

        /*
         * Creates a booking.
         * Rules:
         *  - roomId/reservedBy must be present
         *  - start < end
         *  - start must not be in the past
         *  - no overlaps with existing bookings in the same room
         */
        public Booking CreateBooking(BookingRequest request, DateTimeOffset? now = null)
        {
            var current = now ?? DateTimeOffset.UtcNow;

            ValidateBookingRequest(request, current);

            // Lock per room to prevent race conditions (two requests reserving same room simultaneously)
            var roomLock = _roomLocks.GetOrAdd(request.RoomId, _ => new object());

            lock (roomLock)
            {
                // Overlap condition for [Start, End) intervals:
                // overlap if newStart < existingEnd AND newEnd > existingStart
                var overlaps = _repo.GetByRoom(request.RoomId)
                    .Any(b => request.Start < b.End && request.End > b.Start);

                if (overlaps)
                    throw new BookingConflictException("Huone on jo varattu kyseiselle aikavälille.");

                var booking = new Booking(
                    Id: Guid.NewGuid(),
                    RoomId: request.RoomId,
                    ReservedBy: request.ReservedBy,
                    Start: request.Start,
                    End: request.End
                );

                return _repo.Add(booking);
            }
        }

        // Cancels a booking by ID (returns false if not found)
        public bool CancelBooking(Guid id) => _repo.Remove(id);

        // Returns all bookings for a room
        public IReadOnlyList<Booking> GetBookingsForRoom(string roomId)
        {
            if (string.IsNullOrWhiteSpace(roomId))
                throw new ArgumentException("RoomId puuttuu.");

            return _repo.GetByRoom(roomId);
        }

        /*
         * Returns FREE 24/7 time windows within [rangeStart, rangeEnd),
         * each at least minDuration long.
         *
         * Steps:
         *  1) Fetch all bookings for the room that intersect the range
         *  2) Clamp bookings to the range (cut off outside parts)
         *  3) Merge overlapping/adjacent busy segments
         *  4) Produce gaps between busy segments as free slots
         */
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

            // 1) Only bookings that intersect the search range
            var intersecting = _repo.GetByRoom(roomId)
                .Where(b => b.Start < rangeEnd && b.End > rangeStart)
                .OrderBy(b => b.Start)
                .ToList();

            // 2) Clamp each booking segment into [rangeStart, rangeEnd)
            var busySegments = intersecting
                .Select(b => (
                    Start: Max(b.Start, rangeStart),
                    End: Min(b.End, rangeEnd)
                ))
                .Where(seg => seg.Start < seg.End)
                .OrderBy(seg => seg.Start)
                .ToList();

            // 3) Merge overlapping or adjacent busy segments
            var mergedBusy = new List<(DateTimeOffset Start, DateTimeOffset End)>();
            foreach (var seg in busySegments)
            {
                if (mergedBusy.Count == 0)
                {
                    mergedBusy.Add(seg);
                    continue;
                }

                var last = mergedBusy[^1];

                // Adjacent is treated as continuous busy time: seg.Start <= last.End
                if (seg.Start <= last.End)
                {
                    mergedBusy[^1] = (last.Start, Max(last.End, seg.End));
                }
                else
                {
                    mergedBusy.Add(seg);
                }
            }

            // 4) Produce free slots (gaps) between busy segments
            var freeSlots = new List<FreeSlotDto>();
            var cursor = rangeStart;

            foreach (var busy in mergedBusy)
            {
                if (cursor < busy.Start)
                {
                    var freeStart = cursor;
                    var freeEnd = busy.Start;
                    var duration = freeEnd - freeStart;

                    if (duration >= minDuration)
                        freeSlots.Add(new FreeSlotDto(freeStart, freeEnd, duration.TotalHours));
                }

                cursor = Max(cursor, busy.End);
            }

            // Tail gap (after the last busy segment)
            if (cursor < rangeEnd)
            {
                var duration = rangeEnd - cursor;

                if (duration >= minDuration)
                    freeSlots.Add(new FreeSlotDto(cursor, rangeEnd, duration.TotalHours));
            }

            return freeSlots;
        }

        // Validates booking creation request and returns ALL errors at once
        private static void ValidateBookingRequest(BookingRequest request, DateTimeOffset now)
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

        // Helpers for DateTimeOffset comparisons
        private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
        private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    }
}
