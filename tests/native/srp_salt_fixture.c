/* Actual pairing/SRP code with a deterministic salt generator for regression QA.
 * This fixture is built separately and is never linked into the receiver.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#include <openssl/bn.h>
#include "pairing.c"

static int forced_salt_length = SRP_SALT_SIZE;
static int fixture_bn_rand(BIGNUM *value, int bits, int top, int bottom);
#define BN_rand fixture_bn_rand
#include "srp.c"
#undef BN_rand

static pairing_session_t fixture_session;

static int fixture_bn_rand(BIGNUM *value, int bits, int top, int bottom)
{
    if (bits != 128) {
        return BN_rand(value, bits, top, bottom);
    }
    unsigned char salt[SRP_SALT_SIZE] = {0};
    for (int index = 0; index < forced_salt_length; ++index) {
        salt[SRP_SALT_SIZE - forced_salt_length + index] = (unsigned char)(index + 1);
    }
    return BN_bin2bn(salt, sizeof(salt), value) != NULL;
}

__declspec(dllexport) int clearcast_test_srp_begin(int salt_length, int emulate_old_prefix,
    unsigned char *out_salt, int *out_salt_length, unsigned char *out_public, int *out_public_length)
{
    if (salt_length < 1 || salt_length > SRP_SALT_SIZE) {
        return -1;
    }
    forced_salt_length = salt_length;
    pairing_session_reset_pin(&fixture_session);
    const char *salt = NULL;
    char *public_key = NULL;
    int length = 0, public_length = 0;
    int result = srp_new_user(&fixture_session, NULL, "AA:BB:CC:DD:EE:FF", "1234",
        &salt, &length, &public_key, &public_length);
    if (result) {
        return result;
    }
    if (emulate_old_prefix) {
        salt = (const char *)fixture_session.srp->salt;
    }
    memcpy(out_salt, salt, length);
    *out_salt_length = length;
    memcpy(out_public, public_key, public_length);
    *out_public_length = public_length;
    free(public_key);
    return 0;
}

__declspec(dllexport) int clearcast_test_srp_validate(const unsigned char *public_key,
    int public_length, unsigned char *proof, int proof_length)
{
    return srp_validate_proof(&fixture_session, NULL, public_key, public_length, proof, proof_length);
}

__declspec(dllexport) void clearcast_test_srp_reset(void)
{
    pairing_session_reset_pin(&fixture_session);
}
