#pragma once
#include <array>
#include <cstddef>

namespace gal {
struct Parameters {
    float micHighpassHz = 120.0f;
    float micLowpassHz = 11000.0f;
    float quietGainDb = 6.0f;
    float thresholdDbFs = -24.0f;
    float ratio = 10.0f;
    float kneeDb = 6.0f;
    float attackMs = 0.5f;
    float holdMs = 10.0f;
    float releaseMs = 150.0f;
    float ceiling = 0.98f;
    float wet = 0.0f;
};

class Processor final {
public:
    void reset(float sampleRate, float initialGainDb = 6.0f) noexcept;
    void process(const float* input, float* output, unsigned frames, int inChannels,
                 int outChannels, const Parameters& parameters) noexcept;
    float gainReductionDb() const noexcept { return reductionDb_; }
private:
    static constexpr int MaxChannels = 8;
    struct FilterState { float hpInput=0, hpOutput=0, low=0; };
    std::array<FilterState, MaxChannels> filters_{};
    float sampleRate_ = 48000.0f;
    float gainDb_ = 6.0f;
    float reductionDb_ = 0.0f;
    int holdFrames_ = 0;
};
}
