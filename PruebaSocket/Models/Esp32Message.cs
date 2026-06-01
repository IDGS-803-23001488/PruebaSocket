namespace PruebaSocket.Models;

public class Esp32Message
{
    public long Id { get; set; }

    public int SourceDeviceId { get; set; }

    public Esp32Device SourceDevice { get; set; } = null!;

    public int? TargetDeviceId { get; set; }

    public Esp32Device? TargetDevice { get; set; }

    public string Message { get; set; } = string.Empty;

    public string? Response { get; set; }

    public bool WasProcessed { get; set; }

    public string? ProcessingError { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

