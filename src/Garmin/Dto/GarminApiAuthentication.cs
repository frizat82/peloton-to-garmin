namespace Garmin.Dto;

public class GarminApiAuthentication
{
	public AuthStage AuthStage { get; set; }
	public OAuth2Token OAuth2Token { get; set; }

	/// <summary>Signed in, or at least not waiting on credentials or an MFA code. The token may still need refreshing.</summary>
	public bool IsSignedIn() => AuthStage is not (AuthStage.None or AuthStage.NeedMfaToken);

	public bool IsValid()
	{
		return AuthStage == AuthStage.Completed
			&& OAuth2Token is object
			&& !OAuth2Token.IsExpired();
	}
}

public enum AuthStage : byte
{
	None = 0,
	NeedMfaToken = 1,
	Completed = 2,
}
