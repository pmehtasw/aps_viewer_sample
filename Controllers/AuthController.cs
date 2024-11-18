using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;


[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly APS _aps;

    public AuthController(APS aps)
    {
        _aps = aps;
    }

    [HttpGet("health")]
    public async Task<IActionResult> GetHealth() {
        return Ok(new Dictionary<string, int> {
            { "health", 1 }
        });
    }

    [HttpGet("token")]
    public async Task<IActionResult> GetAccessToken()
    {
        var token = await _aps.GetPublicToken();
        return Ok(new
        {
            access_token = token.AccessToken,
            expires_in = (long)Math.Round((token.ExpiresAt - DateTime.UtcNow).TotalSeconds)
        });
    }
}