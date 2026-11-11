/* Bounded receiver notifications. State floods must not evict fatal or
 * decoder-completion notifications required by the managed lifecycle.
 * SPDX-License-Identifier: GPL-3.0-or-later */
#pragma once
#include <array>
#include <cstddef>

enum ReceiverEventType {
    Ready = 1, Connecting, Video, MirrorEnded, Fatal, PairingRequired, PairingRejected,
    DecoderChanged, DecoderChangeFailed, PairingCompleted, ConnectionRejected,
    PairingAuthenticationRequired
};

struct ReceiverEvent { int type, width, height, codec, error; };

class ClearCastEventQueue {
public:
    void clear() { count_ = 0; }
    bool pop(ReceiverEvent& event) {
        if (!count_) return false;
        event = events_[0];
        erase(0);
        return true;
    }
    void push(ReceiverEvent event) {
        if (critical(event.type)) {
            // One outstanding fatal result and the newest decoder outcome are
            // sufficient; later flood traffic cannot displace either.
            for (size_t i = 0; i < count_; ++i) {
                if (event.type == Fatal ? events_[i].type == Fatal : decoder(events_[i].type)) {
                    events_[i] = event;
                    return;
                }
            }
        } else if (count_) {
            const ReceiverEvent& last = events_[count_ - 1];
            if (last.type == event.type && last.width == event.width && last.height == event.height &&
                last.codec == event.codec && last.error == event.error) return;
        }
        if (count_ == events_.size()) {
            size_t victim = 0;
            while (victim < count_ && critical(events_[victim].type)) ++victim;
            if (victim == count_) return; // At most two critical groups exist.
            erase(victim);
        }
        events_[count_++] = event;
    }
private:
    static bool decoder(int type) { return type == DecoderChanged || type == DecoderChangeFailed; }
    static bool critical(int type) { return type == Fatal || decoder(type); }
    void erase(size_t index) {
        for (size_t i = index + 1; i < count_; ++i) events_[i - 1] = events_[i];
        --count_;
    }
    std::array<ReceiverEvent, 64> events_;
    size_t count_ = 0;
};
