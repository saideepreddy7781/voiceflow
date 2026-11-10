#!/bin/zsh
# Generates the self-signed code-signing identity for VoiceFlow's macOS releases.
#
#   scripts/make-signing-cert.sh ~/voiceflow-signing
#
# The argument is a directory outside this repository. It is created with owner-only permissions.
# The script refuses to write inside this repository or any other git work tree, and it never
# overwrites an existing identity, because a new certificate resets macOS permissions for every user.
#
# Output: voiceflow-signing.p12, its base64 form, its password, and the public certificate.
# The unencrypted private key exists only while the script runs.
#
# This gives builds one stable identity. It is not notarization and does not remove Gatekeeper's
# first-launch "Open Anyway" step. See docs/RELEASING.md.
set -euo pipefail
umask 077

ROOT="${0:A:h:h}"
NAME="VoiceFlow Self-Signed"
DAYS=7300   # 20 years: renewing the certificate changes the identity

if [[ $# -ne 1 || -z "$1" ]]; then
  echo "Usage: $0 <directory outside the repository>" >&2
  exit 2
fi
OUT="${1:A}"

# Find the deepest part of the path that already exists, so the checks run before anything is created.
existing="$OUT"
while [[ ! -d "$existing" ]]; do existing="${existing:h}"; done

if [[ "$OUT/" == "$ROOT/"* ]] || [[ "$(git -C "$existing" rev-parse --is-inside-work-tree 2>/dev/null || true)" == true ]]; then
  echo "Refusing to write signing keys inside a git work tree: $OUT" >&2
  echo "Choose a directory outside the repository." >&2
  exit 1
fi

P12="$OUT/voiceflow-signing.p12"
for f in "$P12" "$P12.base64" "$P12.password" "$OUT/voiceflow-signing.crt"; do
  if [[ -e "$f" ]]; then
    echo "Refusing to overwrite $f. Replacing the certificate resets permissions for every user." >&2
    exit 1
  fi
done

mkdir -p "$OUT"
chmod 700 "$OUT"
WORK="$(mktemp -d "$OUT/.work.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

cat > "$WORK/cert.cnf" <<CNF
[req]
distinguished_name = dn
x509_extensions = ext
prompt = no
[dn]
CN = $NAME
[ext]
basicConstraints = critical, CA:false
keyUsage = critical, digitalSignature
extendedKeyUsage = critical, codeSigning
subjectKeyIdentifier = hash
CNF

# No trailing newline: `gh secret set` stores the file byte for byte.
print -rn -- "$(openssl rand -hex 32)" > "$P12.password"
openssl req -x509 -newkey rsa:3072 -sha256 -days "$DAYS" -nodes \
  -config "$WORK/cert.cnf" -keyout "$WORK/key.pem" -out "$OUT/voiceflow-signing.crt" 2>/dev/null

# macOS `security import` cannot read OpenSSL 3's default PKCS#12 encryption, so these are explicit.
openssl pkcs12 -export -name "$NAME" -inkey "$WORK/key.pem" -in "$OUT/voiceflow-signing.crt" \
  -keypbe PBE-SHA1-3DES -certpbe PBE-SHA1-3DES -macalg sha1 \
  -passout "file:$P12.password" -out "$P12"
openssl pkcs12 -in "$P12" -passin "file:$P12.password" -noout 2>/dev/null
base64 -i "$P12" | tr -d '\n' > "$P12.base64"

FINGERPRINT="$(openssl x509 -in "$OUT/voiceflow-signing.crt" -noout -fingerprint -sha256 | cut -d= -f2)"

cat <<MSG

Created a self-signed code-signing identity in $OUT
  Common name:         $NAME
  SHA-256 fingerprint: $FINGERPRINT

Store these as GitHub repository secrets (Settings > Secrets and variables > Actions):
  MACOS_SIGN_P12           contents of voiceflow-signing.p12.base64
  MACOS_SIGN_P12_PASSWORD  contents of voiceflow-signing.p12.password

With the GitHub CLI, signed in as an account that can administer the repository:
  gh secret set MACOS_SIGN_P12 -R saideepreddy7781/voiceflow < "$P12.base64"
  gh secret set MACOS_SIGN_P12_PASSWORD -R saideepreddy7781/voiceflow < "$P12.password"

Back up voiceflow-signing.p12 and its password somewhere private, such as a password manager. If this
identity is lost, the next release has a new designated requirement and every user has to grant
Microphone and Accessibility again. Never commit any of these files.
MSG
