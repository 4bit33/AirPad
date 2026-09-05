using System.Text;
using AirPad.Core.Logging;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace AirPad.Core.Auth;

/// <summary>
/// Implements the AirPlay pair-setup / pair-verify handshake
/// (Curve25519 ECDH + Ed25519 signatures + AES-CTR binding),
/// following the reverse-engineered flow also used by UxPlay/RPiPlay.
/// Uses BouncyCastle; no Apple private material is embedded.
/// </summary>
public sealed class PairingService
{
    private readonly AirPadLogger _log;
    private readonly SecureRandom _random = new();

    // Long-term server identity (Ed25519). Deterministic default seed keeps
    // the device identity stable across restarts; a random key is generated
    // if the seed is ever replaced.
    private readonly Ed25519PrivateKeyParameters _serverPrivate;
    private readonly Ed25519PublicKeyParameters _serverPublic;

    public byte[] ServerPublicKey { get; }

    public PairingService(AirPadLogger log)
    {
        _log = log;
        var seed = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        _serverPrivate = new Ed25519PrivateKeyParameters(seed, 0);
        _serverPublic = _serverPrivate.GeneratePublicKey();
        ServerPublicKey = _serverPublic.GetEncoded();
    }

    /// <summary>
    /// Step 1 of pair-verify (flag == 1): consumes the client's 32-byte
    /// ECDH public key + 32-byte Ed25519 key, returns our 32-byte ECDH
    /// public key + 64-byte AES-CTR-encrypted signature.
    /// </summary>
    public (byte[] EcdhOurs, byte[] EcdhShared) HandlePairVerifyStep1(
        byte[] ecdhTheirs, byte[] edTheirs,
        out byte[] response)
    {
        var gen = new X25519KeyPairGenerator();
        gen.Init(new X25519KeyGenerationParameters(_random));
        var kp = gen.GenerateKeyPair();
        var oursPriv = (X25519PrivateKeyParameters)kp.Private;
        var oursPub = (X25519PublicKeyParameters)kp.Public;
        byte[] ecdhOurs = oursPub.GetEncoded();

        var theirsPub = new X25519PublicKeyParameters(ecdhTheirs, 0);
        var agreement = new X25519Agreement();
        agreement.Init(oursPriv);
        byte[] shared = new byte[agreement.AgreementSize];
        agreement.CalculateAgreement(theirsPub, shared, 0);

        byte[] toSign = new byte[64];
        Buffer.BlockCopy(ecdhOurs, 0, toSign, 0, 32);
        Buffer.BlockCopy(ecdhTheirs, 0, toSign, 32, 32);

        ISigner signer = SignerUtilities.GetSigner("Ed25519");
        signer.Init(true, _serverPrivate);
        signer.BlockUpdate(toSign, 0, toSign.Length);
        byte[] signature = signer.GenerateSignature();

        var cipher = CreatePairVerifyCipher(shared, forEncryption: true);
        byte[] encrypted = cipher.DoFinal(signature);

        response = new byte[ecdhOurs.Length + encrypted.Length];
        Buffer.BlockCopy(ecdhOurs, 0, response, 0, ecdhOurs.Length);
        Buffer.BlockCopy(encrypted, 0, response, ecdhOurs.Length, encrypted.Length);

        _log.Debug($"pair-verify step1: ECDH agreement established ({shared.Length} bytes)");
        return (ecdhOurs, shared);
    }

    /// <summary>
    /// Step 2 of pair-verify (flag == 0): verifies the client's signature.
    /// <para/>
    /// The handshake uses a SINGLE continuous AES-CTR stream per endpoint:
    /// keystream bytes 0..63 encrypted our step-1 signature, so the client's
    /// step-2 signature arrives encrypted with bytes 64..127. The cipher must
    /// be advanced past the first 64 bytes before decrypting.
    /// </summary>
    public bool HandlePairVerifyStep2(byte[] signature, byte[] ecdhOurs, byte[] ecdhTheirs, byte[] edTheirs, byte[] ecdhShared)
    {
        try
        {
            var cipher = CreatePairVerifyCipher(ecdhShared, forEncryption: true);

            // Burn keystream bytes 0..63 (they encrypted our step-1 signature).
            var discardIn = new byte[64];
            var discardOut = new byte[64];
            cipher.ProcessBytes(discardIn, 0, discardIn.Length, discardOut, 0);

            byte[] decrypted = cipher.DoFinal(signature);

            byte[] message = new byte[64];
            Buffer.BlockCopy(ecdhTheirs, 0, message, 0, 32);
            Buffer.BlockCopy(ecdhOurs, 0, message, 32, 32);

            var clientKey = new Ed25519PublicKeyParameters(edTheirs, 0);
            ISigner verifier = SignerUtilities.GetSigner("Ed25519");
            verifier.Init(false, clientKey);
            verifier.BlockUpdate(message, 0, message.Length);
            bool ok = verifier.VerifySignature(decrypted);
            _log.Info($"pair-verify step2: client signature {(ok ? "VALID" : "INVALID")}");
            return ok;
        }
        catch (Exception ex)
        {
            _log.Error("pair-verify step2 failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Derives the AES-CTR cipher bound to the ECDH shared secret
    /// ("Pair-Verify-AES-Key"/"Pair-Verify-AES-IV" + SHA-512).
    /// </summary>
    public static IBufferedCipher CreatePairVerifyCipher(byte[] ecdhShared, bool forEncryption)
    {
        byte[] keySeed = Encoding.UTF8.GetBytes("Pair-Verify-AES-Key");
        byte[] ivSeed = Encoding.UTF8.GetBytes("Pair-Verify-AES-IV");
        using var sha = System.Security.Cryptography.SHA512.Create();
        byte[] keyHash = sha.ComputeHash(keySeed.Concat(ecdhShared).ToArray());
        byte[] ivHash = sha.ComputeHash(ivSeed.Concat(ecdhShared).ToArray());

        var cipher = CipherUtilities.GetCipher("AES/CTR/NoPadding");
        var keyParam = ParameterUtilities.CreateKeyParameter("AES", keyHash, 0, 16);
        cipher.Init(forEncryption, new ParametersWithIV(keyParam, ivHash, 0, 16));
        return cipher;
    }
}
