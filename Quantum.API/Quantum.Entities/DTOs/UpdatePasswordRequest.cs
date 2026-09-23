namespace Quantum.Entities.DTOs;

public class UpdatePasswordRequest
{
    public string OldUserName { get; set; }

    public string NewUserName { get; set; }

    public string OldPassword { get; set; }

    public string NewPassword { get; set; }
}
