using System.Security.Cryptography;
using System.Text;
using Dp9ik;

namespace NinePSharp.Fog.Auth;

// factotum's p9any server role, offering only dp9ik, as p9any.c and p9sk1.c run it. A failed write
// leaves the exchange in the phase it was in, as factotum does.
internal sealed class P9anyServer
{
    private const string NegotiationFailed = "negotiation failed, no common protocols or keys";
    private const string ProtocolBotch = "auth server protocol botch";
    private const int Form0TicketLength = 1 + Dp9ikConstants.ChallengeLength + (2 * Dp9ikConstants.NameLength) + Dp9ikConstants.DesKeyLength;
    private const int Form0AuthenticatorLength = 1 + Dp9ikConstants.ChallengeLength + 4;
    private const int Form1TicketLength = 12 + Dp9ikConstants.ChallengeLength + (2 * Dp9ikConstants.NameLength) + Dp9ikConstants.NonceLength + 16;
    private const int Form1AuthenticatorLength = 12 + Dp9ikConstants.ChallengeLength + Dp9ikConstants.NonceLength + 16;
    private static readonly string[] Form1Signatures = ["form1 PR", "form1 Ts", "form1 Tc", "form1 As", "form1 Ac", "form1 Tp", "form1 Hr"];
    private readonly AuthKey? key;
    private readonly string authId;
    private readonly string authDomain;
    private readonly byte[] challenge = RandomNumberGenerator.GetBytes(Dp9ikConstants.ChallengeLength);
    private readonly AuthPakState pak = new();
    private byte[] clientChallenge = [];
    private Ticket? ticket;
    private Phase phase = Phase.SHaveProtos;

    internal P9anyServer(AuthKey? key, string authId, string authDomain)
    {
        this.key = key;
        this.authId = authId;
        this.authDomain = authDomain;
    }

    private enum Phase
    {
        SHaveProtos,
        SNeedProto,
        SNeedChal,
        SHavePAKreq,
        SNeedPAKy,
        SNeedTicket,
        SHaveAuth,
        Established,
    }

    internal string? ClientUser => ticket?.ClientUserText;

    // The next message, or null once the exchange is established.
    internal byte[]? Read()
    {
        switch (phase)
        {
            case Phase.SHaveProtos:
                if (key is null)
                {
                    throw new P9anyException(NegotiationFailed);
                }

                phase = Phase.SNeedProto;
                return Encoding.UTF8.GetBytes($"dp9ik@{authDomain}\0");
            case Phase.SHavePAKreq:
                var request = new TicketRequest(AuthMessageType.AuthPak);
                request.SetAuthId(authId);
                request.SetAuthDomain(authDomain);
                request.SetChallenge(challenge);
                phase = Phase.SNeedPAKy;
                return [.. request.Marshal(), .. pak.CreatePublicValue(key!, isClient: true)];
            case Phase.SHaveAuth:
                var authenticator = new Authenticator(AuthMessageType.AuthAs);
                authenticator.SetChallenge(clientChallenge);
                authenticator.SetRandom(RandomNumberGenerator.GetBytes(Dp9ikConstants.NonceLength));
                phase = Phase.Established;
                return authenticator.Marshal(ticket!);
            case Phase.Established:
                return null;
            default:
                // lib9p's authread reports any refused read as a botch, so it needs no message.
                throw new P9anyException();
        }
    }

    internal void Write(ReadOnlySpan<byte> data)
    {
        switch (phase)
        {
            case Phase.SNeedProto:
                ChooseProtocol(data);
                break;
            case Phase.SNeedChal:
                clientChallenge = Need(data, Dp9ikConstants.ChallengeLength).ToArray();
                phase = Phase.SHavePAKreq;
                break;
            case Phase.SNeedPAKy:
                FinishPak(Need(data, Dp9ikConstants.PakPublicValueLength));
                break;
            case Phase.SNeedTicket:
                AcceptTicket(data);
                break;
            case Phase.Established:
                throw new P9anyException("authentication already done");
            default:
                throw PhaseError();
        }
    }

    private static ReadOnlySpan<byte> Need(ReadOnlySpan<byte> data, int length) =>
        data.Length < length ? throw P9anyException.TooSmall() : data[..length];

    private static bool Form1Signed(ReadOnlySpan<byte> data) => Form1Signatures.Contains(Encoding.ASCII.GetString(data[..8]));

    private void ChooseProtocol(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0 || data[^1] != 0)
        {
            throw P9anyException.TooSmall();
        }

        string[] tokens = Encoding.UTF8.GetString(data[..^1]).Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 2)
        {
            throw new P9anyException("invalid argument");
        }

        string protocol = tokens[0].Split('@')[0];
        if (protocol != "dp9ik" || tokens[1] != authDomain)
        {
            throw new P9anyException(NegotiationFailed);
        }

        phase = Phase.SNeedChal;
    }

    private void FinishPak(ReadOnlySpan<byte> value)
    {
        try
        {
            pak.Finish(key!, value);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new P9anyException(ProtocolBotch);
        }

        phase = Phase.SNeedTicket;
    }

    // convM2T and convM2A ask for more bytes when short; dp9ik refuses a form 0 ticket.
    private void AcceptTicket(ReadOnlySpan<byte> data)
    {
        if (data.Length < Form0TicketLength + Form0AuthenticatorLength)
        {
            throw P9anyException.TooSmall();
        }

        bool form1 = Form1Signed(data);
        if (form1 && data.Length < Form1TicketLength + Form1AuthenticatorLength)
        {
            throw P9anyException.TooSmall();
        }

        if (!form1 ||
            !Ticket.TryUnmarshal(key!, data, out Ticket? accepted, out _) ||
            accepted!.Type != AuthMessageType.AuthTs ||
            !CryptographicOperations.FixedTimeEquals(accepted.Challenge, challenge) ||
            !Authenticator.TryUnmarshal(accepted, data[Form1TicketLength..], out Authenticator? authenticator, out _) ||
            authenticator!.Type != AuthMessageType.AuthAc ||
            !CryptographicOperations.FixedTimeEquals(authenticator.Challenge, challenge))
        {
            throw new P9anyException(ProtocolBotch);
        }

        ticket = accepted;
        phase = Phase.SHaveAuth;
    }

    private P9anyException PhaseError() => new($"protocol phase error: write in state {phase}", isPhaseError: true);
}
