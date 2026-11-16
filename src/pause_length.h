#ifndef PHONON_PAUSE_LENGTH_H
#define PHONON_PAUSE_LENGTH_H

#include <math.h>
#include <stdbool.h>
#include <stddef.h>
#include <string.h>

typedef bool (*PauseSink)(void *user, const float *pcm, size_t count);

typedef enum { PAUSE_DEFAULT, PAUSE_SHORTER, PAUSE_LONGER } PauseMode;
enum {
    PAUSE_SAMPLE_RATE = 24000,
    PAUSE_FRAME = PAUSE_SAMPLE_RATE * 5 / 1000,
    PAUSE_HEAD = 19,
    PAUSE_TAIL = 5,
    PAUSE_LONG_MIN = 24,
    PAUSE_LONG_EXTRA = 16 * PAUSE_FRAME,
};

typedef struct {
    PauseMode mode;
    bool heard_sound;
    size_t quiet_frames, partial_count, tail_start, tail_count;
    float partial[PAUSE_FRAME];
    float tail[PAUSE_TAIL][PAUSE_FRAME];
} PauseLength;

static inline bool pause_tail(PauseLength *state, PauseSink output, void *user) {
    while (state->tail_count) {
        if (!output(user, state->tail[state->tail_start], PAUSE_FRAME)) return false;
        state->tail_start = (state->tail_start + 1) % PAUSE_TAIL;
        --state->tail_count;
    }
    return true;
}

static inline bool pause_frame(PauseLength *state, PauseSink output, void *user) {
    const float limit = 96.f / 32767.f;
    bool audible = false;
    for (size_t i = 0; i < PAUSE_FRAME; ++i) {
        if (isfinite(state->partial[i]) && fabsf(state->partial[i]) > limit) {
            audible = true;
            break;
        }
    }
    if (audible) {
        if (!pause_tail(state, output, user)) return false;
        if (state->mode == PAUSE_LONGER && state->quiet_frames >= PAUSE_LONG_MIN) {
            static const float space[PAUSE_LONG_EXTRA] = {0};
            if (!output(user, space, PAUSE_LONG_EXTRA)) return false;
        }
        state->quiet_frames = 0;
        state->heard_sound = true;
    } else if (state->heard_sound && ++state->quiet_frames > PAUSE_HEAD && state->mode == PAUSE_SHORTER) {
        size_t position = (state->tail_start + state->tail_count) % PAUSE_TAIL;
        memcpy(state->tail[position], state->partial, sizeof(state->partial));
        if (state->tail_count < PAUSE_TAIL) ++state->tail_count;
        else state->tail_start = (state->tail_start + 1) % PAUSE_TAIL;
        return true;
    }
    return output(user, state->partial, PAUSE_FRAME);
}

static inline bool pause_push(PauseLength *state, const float *pcm, size_t count, PauseSink output, void *user) {
    if (state->mode == PAUSE_DEFAULT) return !count || output(user, pcm, count);
    while (count) {
        size_t n = PAUSE_FRAME - state->partial_count;
        if (n > count) n = count;
        memcpy(state->partial + state->partial_count, pcm, n * sizeof(float));
        state->partial_count += n;
        if (state->partial_count == PAUSE_FRAME) {
            state->partial_count = 0;
            if (!pause_frame(state, output, user)) return false;
        }
        pcm += n;
        count -= n;
    }
    return true;
}

static inline bool pause_finish(PauseLength *state, PauseSink output, void *user) {
    return pause_tail(state, output, user) &&
           (!state->partial_count || output(user, state->partial, state->partial_count));
}

#endif
