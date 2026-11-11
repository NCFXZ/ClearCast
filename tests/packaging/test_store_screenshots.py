"""Regression checks for invisible Windows resize gutters in Store captures."""
import importlib.util
from pathlib import Path
import unittest

SCRIPT = Path(__file__).resolve().parents[2] / "scripts/packaging/Prepare-StoreScreenshots.py"
SPEC = importlib.util.spec_from_file_location("store_screenshot_preparation", SCRIPT)
PREPARE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PREPARE)


class StoreScreenshotEdges(unittest.TestCase):
    @staticmethod
    def frame(left=0, right=0, bottom=0):
        width, height = 120, 80
        data = bytearray(bytes((38, 54, 74)) * (width * height))
        for y in range(height):
            for x in range(width):
                if x < left or x >= width - right or y >= height - bottom:
                    data[(y * width + x) * 3:(y * width + x) * 3 + 3] = b"\0\0\0"
        return width, height, data

    def test_original_three_sided_resize_frame_is_detected(self):
        self.assertEqual(PREPARE.black_gutters(*self.frame(12, 12, 12)),
                         {"left": 12, "right": 12, "top": 0, "bottom": 12})

    def test_visible_frame_has_no_black_gutters(self):
        self.assertEqual(PREPARE.black_gutters(*self.frame()),
                         {"left": 0, "right": 0, "top": 0, "bottom": 0})

    def test_one_pixel_native_border_is_distinct_from_resize_gutter(self):
        self.assertEqual(max(PREPARE.black_gutters(*self.frame(1, 1, 1)).values()), 1)

    def test_decoder_cannot_silently_supply_a_truncated_frame(self):
        with self.assertRaises(ValueError):
            PREPARE.black_gutters(120, 80, bytes(120 * 80 * 3 - 1))

    def test_complete_language_and_view_inventory(self):
        self.assertEqual(len(PREPARE.EXPECTED), 16)
        self.assertEqual(sum(name.startswith("en-US-") for name in PREPARE.EXPECTED), 8)
        self.assertEqual(sum(name.startswith("zh-CN-") for name in PREPARE.EXPECTED), 8)

    def test_mixed_window_and_display_dimensions_are_rejected(self):
        records = [{"file": name, "width": 2880, "height": 1800} for name in sorted(PREPARE.EXPECTED)]
        PREPARE.validate_capture_set(records)
        records[0]["width"] = 1920
        records[0]["height"] = 1080
        with self.assertRaises(ValueError):
            PREPARE.validate_capture_set(records)


if __name__ == "__main__":
    unittest.main()
