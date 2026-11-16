#include "../src/pause_length.h"

#include <algorithm>
#include <cassert>
#include <vector>

static bool collect(void* user, const float* audio, size_t count) {
    auto& result = *static_cast<std::vector<float>*>(user);
    result.insert(result.end(), audio, audio + count);
    return true;
}

static std::vector<float> filter(PauseMode mode, const std::vector<float>& input) {
    PauseLength state{};
    state.mode = mode;
    std::vector<float> out;
    const size_t chunk = 97;
    for (size_t i = 0; i < input.size(); i += chunk) {
        assert(pause_push(&state, input.data() + i, std::min(chunk, input.size() - i), collect, &out));
    }
    assert(pause_finish(&state, collect, &out));
    return out;
}

int main() {
    const size_t leading = 24 * PAUSE_FRAME;
    const size_t gap = 38 * PAUSE_FRAME;
    std::vector<float> input(leading);
    input.insert(input.end(), PAUSE_FRAME, 0.1f);
    input.insert(input.end(), gap, 0.f);
    input.insert(input.end(), PAUSE_FRAME + 3, -0.1f);
    assert(filter(PAUSE_DEFAULT, input) == input);

    auto shorter = filter(PAUSE_SHORTER, input);
    assert(shorter.size() == input.size() - gap + (PAUSE_HEAD + PAUSE_TAIL) * PAUSE_FRAME);
    assert(shorter[leading - 1] == 0.f && shorter[leading] == 0.1f && shorter.back() == -0.1f);
    auto longer = filter(PAUSE_LONGER, input);
    assert(longer.size() == input.size() + PAUSE_LONG_EXTRA);
    assert(longer[leading - 1] == 0.f && longer[leading] == 0.1f && longer.back() == -0.1f);

    const std::vector<float> silent(leading + 3, 0.f);
    assert(filter(PAUSE_SHORTER, silent) == silent);
    assert(filter(PAUSE_LONGER, silent) == silent);

    PauseLength cancelled{};
    cancelled.mode = PAUSE_SHORTER;
    const auto reject = [](void*, const float*, size_t) { return false; };
    assert(!pause_push(&cancelled, input.data(), input.size(), reject, nullptr));
}
