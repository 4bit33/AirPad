using AirPad.Core.Auth;
using AirPad.Core.Logging;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace AirPad.Core.Tests;

public sealed class PairingTests
{
    private static PairingService Create() => new(new AirPadLogger(minimumLevel: LogLevel.Error));

    [Fact]
    public void ServerPublicKey_Is32Bytes()
    {
        var pairing = Create();
        Assert.Equal(32, pairing.ServerPublicKey.Length);
    }

    [Fact]
    public void PairVerifyStep1_Returns96ByteResponseAnd32ByteSecret()
    {
        var pairing = Create();
        var ecdhTheirs = new byte[32];
        var edTheirs = new byte[32];
        new Random(42).NextBytes(ecdhTheirs);
        new Random(7).NextBytes(edTheirs);

        var (ecdhOurs, shared) = pairing.HandlePairVerifyStep1(ecdhTheirs, edTheirs, out var response);

        Assert.Equal(32, ecdhOurs.Length);
        Assert.Equal(32, shared.Length);
        Assert.Equal(96, response.Length); // 32 ECDH pub + 64 encrypted signature
    }

    [Fact]
    public void PairVerifyStep1_IsRandomizedPerCall()
    {
        var pairing = Create();
        var theirs1 = new byte[32];
        var theirs2 = new byte[32];
        new Random(11).NextBytes(theirs1);
        new Random(22).NextBytes(theirs2);
        pairing.HandlePairVerifyStep1(theirs1, new byte[32], out var r1);
        pairing.HandlePairVerifyStep1(theirs2, new byte[32], out var r2);
        Assert.NotEqual(r1, r2);
    }

    [Fact]
    public void FairPlay_Phase1_Returns142BytesPerMode()
    {
        var fp = new FairPlayService(new AirPadLogger(minimumLevel: LogLevel.Error));
        for (int mode = 0; mode < 4; mode++)
        {
            var body = new byte[16];
            body[4] = 0x03;
            body[14] = (byte)mode;
            var reply = fp.Handle(body, out var keyMsg);
            Assert.NotNull(reply);
            Assert.Equal(142, reply!.Length);
            Assert.Null(keyMsg);
        }
    }

    [Fact]
    public void FairPlay_Phase2_Returns32BytesAndStoresKey()
    {
        var fp = new FairPlayService(new AirPadLogger(minimumLevel: LogLevel.Error));
        var body = new byte[164];
        body[4] = 0x03;
        for (int i = 144; i < 164; i++)
            body[i] = (byte)i;

        var reply = fp.Handle(body, out var keyMsg);
        Assert.NotNull(reply);
        Assert.Equal(32, reply!.Length);
        Assert.NotNull(keyMsg);
        Assert.Equal(164, keyMsg!.Length);
        // FPLY header + echoed tail.
        Assert.Equal(new byte[] { 0x46, 0x50, 0x4C, 0x59 }, reply.Take(4).ToArray());
        Assert.Equal(body.Skip(144).ToArray(), reply.Skip(12).ToArray());
    }

    [Fact]
    public void FairPlay_RejectsGarbage()
    {
        var fp = new FairPlayService(new AirPadLogger(minimumLevel: LogLevel.Error));
        Assert.Null(fp.Handle(new byte[17], out _));
        Assert.Null(fp.Handle(Array.Empty<byte>(), out _));
    }

    [Fact]
    public void PairVerify_FullHandshake_WithContinuousCtrStream_Succeeds()
    {
        // Emulates a real AirPlay client speaking the continuous-stream
        // pair-verify dialect: one AES-CTR stream whose bytes 0..63 carry the
        // server's step-1 signature and bytes 64..127 carry the client's
        // step-2 signature.
        var pairing = Create();
        var random = new SecureRandom();

        // Client long-term keys.
        var xGen = new X25519KeyPairGenerator();
        xGen.Init(new X25519KeyGenerationParameters(random));
        var xKp = xGen.GenerateKeyPair();
        var xPriv = (X25519PrivateKeyParameters)xKp.Private;
        var xPub = (X25519PublicKeyParameters)xKp.Public;
        byte[] ecdhTheirs = xPub.GetEncoded();

        var edGen = new Ed25519KeyPairGenerator();
        edGen.Init(new Ed25519KeyGenerationParameters(random));
        var edKp = edGen.GenerateKeyPair();
        var edPriv = (Ed25519PrivateKeyParameters)edKp.Private;
        var edPub = (Ed25519PublicKeyParameters)edKp.Public;
        byte[] edTheirs = edPub.GetEncoded();

        // Server step 1.
        var (ecdhOurs, sharedS) = pairing.HandlePairVerifyStep1(ecdhTheirs, edTheirs, out var response);
        Assert.Equal(96, response.Length);

        // Client: ECDH shared secret.
        var agree = new X25519Agreement();
        agree.Init(xPriv);
        byte[] sharedC = new byte[agree.AgreementSize];
        agree.CalculateAgreement(new X25519PublicKeyParameters(ecdhOurs, 0), sharedC, 0);
        Assert.Equal(sharedS, sharedC);

        // Client: single continuous cipher; decrypt server signature (bytes 0..63).
        // NOTE: BouncyCastle resets the CTR counter on DoFinal, so a faithful
        // continuous stream must use ProcessBytes (like UxPlay's aes_ctr kept
        // alive across both messages).
        var clientCipher = PairingService.CreatePairVerifyCipher(sharedC, forEncryption: true);
        var serverEnc = response.Skip(32).ToArray();
        var serverSig = new byte[64];
        Assert.Equal(64, clientCipher.ProcessBytes(serverEnc, 0, serverEnc.Length, serverSig, 0));

        ISigner verifier = SignerUtilities.GetSigner("Ed25519");
        verifier.Init(false, new Ed25519PublicKeyParameters(pairing.ServerPublicKey, 0));
        byte[] signedByServer = ecdhOurs.Concat(ecdhTheirs).ToArray();
        verifier.BlockUpdate(signedByServer, 0, signedByServer.Length);
        Assert.True(verifier.VerifySignature(serverSig));

        // Client: sign (theirs||ours), encrypt with stream bytes 64..127.
        ISigner signer = SignerUtilities.GetSigner("Ed25519");
        signer.Init(true, edPriv);
        byte[] toSign = ecdhTheirs.Concat(ecdhOurs).ToArray();
        signer.BlockUpdate(toSign, 0, toSign.Length);
        byte[] clientSig = signer.GenerateSignature();
        var clientSigEncrypted = new byte[64];
        Assert.Equal(64, clientCipher.ProcessBytes(clientSig, 0, clientSig.Length, clientSigEncrypted, 0));

        // Server step 2 must accept it.
        Assert.True(pairing.HandlePairVerifyStep2(clientSigEncrypted, ecdhOurs, ecdhTheirs, edTheirs, sharedS));
    }

    [Fact]
    public void FairPlayTables_AreLoadedFromEmbeddedResources()
    {
        Assert.NotEmpty(OmgHaxHex.TableS1);
        Assert.NotEmpty(OmgHaxHex.TableS2);
        Assert.NotEmpty(OmgHaxHex.TableS3);
        Assert.NotEmpty(OmgHaxHex.TableS4);
        Assert.NotEmpty(OmgHaxHex.TableS5);
        Assert.NotEmpty(OmgHaxHex.TableS9);
        Assert.NotEmpty(OmgHaxHex.TableS10);
    }
}
