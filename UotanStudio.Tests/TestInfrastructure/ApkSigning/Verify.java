import com.android.apksig.ApkVerifier;
import java.io.File;
public class Verify {
  public static void main(String[] args) throws Exception {
    if (args[0].equals("sign")) {
      String pem = java.nio.file.Files.readString(java.nio.file.Path.of(args[3]))
        .replace("-----BEGIN PRIVATE KEY-----", "").replace("-----END PRIVATE KEY-----", "").replaceAll("\\s", "");
      java.security.PrivateKey key = java.security.KeyFactory.getInstance(args[5]).generatePrivate(
        new java.security.spec.PKCS8EncodedKeySpec(java.util.Base64.getDecoder().decode(pem)));
      java.security.cert.X509Certificate cert;
      try (java.io.InputStream input = java.nio.file.Files.newInputStream(java.nio.file.Path.of(args[4]))) {
        cert = (java.security.cert.X509Certificate)java.security.cert.CertificateFactory.getInstance("X.509").generateCertificate(input);
      }
      com.android.apksig.ApkSigner.SignerConfig signer = new com.android.apksig.ApkSigner.SignerConfig.Builder(
        "UPSTREAM", key, java.util.List.of(cert)).build();
      new com.android.apksig.ApkSigner.Builder(java.util.List.of(signer)).setInputApk(new File(args[1]))
        .setOutputApk(new File(args[2])).setMinSdkVersion(24).setV1SigningEnabled(true)
        .setV2SigningEnabled(true).setV3SigningEnabled(true).setV4SigningEnabled(false).build().sign();
      System.out.println("upstream signed");
      return;
    }
    ApkVerifier.Result r = new ApkVerifier.Builder(new File(args[0]))
      .setMinCheckedPlatformVersion(Integer.parseInt(args[1]))
      .setMaxCheckedPlatformVersion(Integer.parseInt(args[2])).build().verify();
    System.out.println(new File(args[0]).getName()+" verified="+r.isVerified()+" v1="+r.isVerifiedUsingV1Scheme()+" v2="+r.isVerifiedUsingV2Scheme()+" v3="+r.isVerifiedUsingV3Scheme());
    for (Object e : r.getAllErrors()) System.out.println("ERROR: "+e);
    for (Object e : r.getWarnings()) System.out.println("WARNING: "+e);
    if (!r.isVerified()) System.exit(1);
  }
}
