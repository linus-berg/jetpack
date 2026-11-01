#!/usr/bin/env bash
# Creates an internal CA and a TLS certificate that lets Jetpack answer for
# plugins.jetbrains.com (and any extra names you pass).
#
#   ./generate-certs.sh                         # JetBrains names only
#   ./generate-certs.sh jetpack.corp.example 10.1.2.3
#
# Output (in ./certs, or $CERT_DIR):
#   ca.crt        distribute to every developer machine's OS trust store
#   ca.key        keep offline / secret; only needed to issue new certificates
#   jetpack.crt   server certificate (with the CA appended, full chain)
#   jetpack.key   server private key
#
# If your organisation already runs a CA, issue a certificate for the same
# names from it instead and skip this script.
set -euo pipefail

CERT_DIR="${CERT_DIR:-$(dirname "$0")/certs}"
DAYS="${DAYS:-825}"
mkdir -p "$CERT_DIR"
cd "$CERT_DIR"

names=(plugins.jetbrains.com downloads.marketplace.jetbrains.com "$@")
san=""
for name in "${names[@]}"; do
  if [[ "$name" =~ ^[0-9.]+$ || "$name" == *:* ]]; then
    san+="IP:${name},"
  else
    san+="DNS:${name},"
  fi
done
san="${san%,}"

if [[ ! -f ca.key ]]; then
  echo "Creating CA (ca.crt / ca.key)"
  openssl req -x509 -newkey rsa:4096 -sha256 -days 3650 -nodes \
    -keyout ca.key -out ca.crt \
    -subj "/CN=Jetpack Internal CA" \
    -addext "basicConstraints=critical,CA:TRUE" \
    -addext "keyUsage=critical,keyCertSign,cRLSign" >/dev/null 2>&1
  chmod 600 ca.key
else
  echo "Reusing existing CA (ca.crt / ca.key)"
fi

echo "Issuing server certificate for: ${names[*]}"
openssl req -newkey rsa:2048 -sha256 -nodes \
  -keyout jetpack.key -out jetpack.csr \
  -subj "/CN=${names[0]}" >/dev/null 2>&1
cat > jetpack.ext <<EOF
basicConstraints=CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectAltName=${san}
EOF
# 825 days is the maximum validity Apple platforms accept for TLS certificates.
openssl x509 -req -in jetpack.csr -CA ca.crt -CAkey ca.key -CAcreateserial \
  -days "$DAYS" -sha256 -extfile jetpack.ext -out jetpack.leaf.crt >/dev/null 2>&1
cat jetpack.leaf.crt ca.crt > jetpack.crt
rm -f jetpack.csr jetpack.ext jetpack.leaf.crt ca.srl
# The container runs as an unprivileged user (uid 1654) and must read the key.
chmod 644 jetpack.key jetpack.crt ca.crt

echo
echo "Done. Next steps:"
echo "  1. Install $(pwd)/ca.crt into the OS trust store of every developer machine"
echo "     (JetBrains IDEs trust the OS store by default)."
echo "  2. Point plugins.jetbrains.com and downloads.marketplace.jetbrains.com at"
echo "     the Jetpack host in your internal DNS."
echo "  3. Start Jetpack with jetpack.crt / jetpack.key (see docker-compose.yml)."
