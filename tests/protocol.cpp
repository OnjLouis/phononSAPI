// SPDX-License-Identifier: GPL-3.0-or-later
#include "../src/protocol.h"
#include <cassert>
#include <limits>
int wmain() {
    phonon::Request r;
    r.textBytes = 12; r.voiceBytes = 5; r.languageBytes = 2;
    assert(phonon::valid(r));
    r.textBytes = 0; assert(!phonon::valid(r));
    r.textBytes = phonon::MaxText + 1; assert(!phonon::valid(r));
    r.textBytes = 12; r.speed = std::numeric_limits<float>::quiet_NaN();
    assert(!phonon::valid(r));
    r.speed = 1; r.voiceBytes = 513; assert(!phonon::valid(r));
    r.voiceBytes = 5; r.languageBytes = 9; assert(!phonon::valid(r));
    r.languageBytes = 2; r.gpu = 2; assert(!phonon::valid(r));
    assert(phonon::utf8(L"D\u00e9bora") == "D\xc3\xa9" "bora");
    return 0;
}
