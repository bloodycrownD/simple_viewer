import sys, shutil, json
import time

from natsort import natsorted

from PySide6.QtWidgets import (
    QMainWindow, QApplication, QLabel, QFileDialog,
    QToolBar, QStatusBar, QWidget, QHBoxLayout, QMessageBox
)
from PySide6.QtGui import QAction, QPixmap, QImage, QKeyEvent, QResizeEvent
from PySide6.QtCore import Qt, QTimer
from PIL import Image, ImageSequence
import os
from collections import OrderedDict


class LRUCache:
    def __init__(self, capacity):
        self.cache = OrderedDict()
        self.capacity = capacity

    def get(self, key):
        if key not in self.cache:
            return -1
        else:
            self.cache.move_to_end(key)
            return self.cache[key]

    def put(self, key, value) -> None:
        if key in self.cache:
            self.cache.move_to_end(key)
        self.cache[key] = value
        if len(self.cache) > self.capacity:
            _, img = self.cache.popitem(last=False)
            img.close()

    def has(self, key) -> bool:
        return key in self.cache

    def remove(self, key) -> None:
        if key in self.cache:
            self.cache.pop(key).close()

    def clear(self) -> None:
        for _, img in self.cache.items():
            img.close()


class ImageViewer(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("Simple Image Viewer")
        self.setMinimumSize(800, 600)
        self.config = self.load_config()

        # 初始化变量
        self.current_image_path = None
        self.image_files = []
        self.current_image_index = -1
        self.rotation_angle = 0
        self.is_fullscreen = False
        self.original_pixmap = None  # 保存原始图片
        # 缓存半径
        self.cache_radius = self.config["cache_radius"]
        self.image_cache = LRUCache(self.cache_radius * 2 + 1)

        # GIF支持
        self.gif_frames = []
        self.current_gif_frame = 0
        self.gif_timer = QTimer(self)
        self.gif_timer.timeout.connect(self.next_gif_frame)

        # 创建界面
        self.create_ui()

    def get_current_image_path(self):
        if self.image_files:
            return self.image_files[self.current_image_index]

    def load_config(self):
        path = os.path.join(os.path.dirname(__file__), "config.json")
        with open(path, 'r') as f:
            config = json.load(f)
        return config

    def create_ui(self):
        # 主窗口部件
        central_widget = QWidget()
        self.setCentralWidget(central_widget)
        self.layout = QHBoxLayout(central_widget)

        # 图片显示标签
        self.image_label = QLabel()
        self.image_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.image_label.setScaledContents(False)  # 禁止自动拉伸
        self.layout.addWidget(self.image_label)

        # 工具栏
        toolbar = QToolBar()
        self.addToolBar(toolbar)

        # 动作：打开文件
        open_action = QAction("Open", self)
        open_action.triggered.connect(self.open_image)
        toolbar.addAction(open_action)

        # 动作：上一张/下一张
        prev_action = QAction("Prev", self)
        prev_action.triggered.connect(self.prev_image)
        toolbar.addAction(prev_action)

        next_action = QAction("Next", self)
        next_action.triggered.connect(self.next_image)
        toolbar.addAction(next_action)

        # 旋转
        rotate_left_action = QAction("Rotate Left", self)
        rotate_left_action.triggered.connect(lambda: self.rotate_image(-90))
        toolbar.addAction(rotate_left_action)

        rotate_right_action = QAction("Rotate Right", self)
        rotate_right_action.triggered.connect(lambda: self.rotate_image(90))
        toolbar.addAction(rotate_right_action)

        # 动作：删除图片
        delete_action = QAction("Delete", self)
        delete_action.triggered.connect(self.delete_image)
        toolbar.addAction(delete_action)

        # 状态栏
        self.status_bar = QStatusBar()
        self.setStatusBar(self.status_bar)

    def open_image(self):
        file, _ = QFileDialog.getOpenFileName(
            self, "Open Image", "",
            "Images (*.png *.jpg *.jpeg *.gif)"
        )
        if file:
            directory = os.path.dirname(file)
            # 使路径名格式一致
            base_name = os.path.basename(file)
            file = os.path.join(directory, base_name)
            self.image_files = [
                os.path.join(directory, f) for f in os.listdir(directory)
                if f.lower().endswith(('.png', '.jpg', '.jpeg', '.gif'))
            ]
            self.image_files = natsorted(self.image_files)
            self.current_image_index = self.image_files.index(file)
            self.init_image_cache(self.image_files, self.current_image_index)
            self.load_image(file)

    def get_right_edge(self):
        if self.image_files and self.cache_radius * 2 + 1 < len(self.image_files):
            return (self.current_image_index + self.cache_radius) % len(self.image_files)

    def get_left_edge(self):
        if self.image_files and self.cache_radius * 2 + 1 < len(self.image_files):
            left_edge = self.current_image_index - self.cache_radius
            return left_edge if left_edge >= 0 else left_edge + len(self.image_files)

    def load_image_to_cache(self, path):
        img = Image.open(path)
        self.image_cache.put(path, img)

    def init_image_cache(self, image_files, current_index):
        # 清空缓存
        self.image_cache.clear()
        # 缓存半径内缓存所有图片
        if len(image_files) < self.cache_radius * 2 + 1:
            [self.load_image_to_cache(path) for path in image_files]
        else:
            # 不支持越界索引
            for index in range(current_index, self.cache_radius + current_index + 1):
                self.load_image_to_cache(image_files[index % len(image_files)])
            # 支持负索引
            for index in range(current_index - self.cache_radius, current_index):
                self.load_image_to_cache(image_files[index % len(image_files)])

    def load_image(self, path):
        # 停止GIF动画
        self.gif_timer.stop()
        if self.image_cache.has(path):
            img = self.image_cache.get(path)
        else:
            img = Image.open(path)
            self.image_cache.put(path, img)

        if img.format == 'GIF':
            # 处理动态GIF
            self.gif_frames = []
            for frame in ImageSequence.Iterator(img):
                self.gif_frames.append(frame.copy().convert("RGBA"))
            self.current_gif_frame = 0
            self.start_gif_animation()
        else:
            # 静态图片
            img = img.convert("RGBA")
            self.display_pil_image(img)

        # 更新状态栏信息
        self.update_status_bar(path)

    def update_status_bar(self, path):
        # 获取图片信息
        file_name = os.path.basename(path)
        file_size = os.path.getsize(path) / 1024  # 转换为KB
        if file_size > 1024:
            file_size = f"{file_size / 1024:.2f} MB"
        else:
            file_size = f"{file_size:.2f} KB"

        with Image.open(path) as img:
            width, height = img.size

        # 图片索引
        index_info = f"{self.current_image_index + 1}/{len(self.image_files)}"

        # 更新状态栏
        self.status_bar.showMessage(
            f"Name: {file_name} | Size: {file_size} | Dimensions: {width}x{height} | Index: {index_info}"
        )

    def start_gif_animation(self):
        if self.gif_frames:
            self.gif_timer.start(100)  # 按帧延迟调整（示例使用固定100ms）

    def next_gif_frame(self):
        if self.gif_frames:
            frame = self.gif_frames[self.current_gif_frame]
            self.display_pil_image(frame)
            self.current_gif_frame = (self.current_gif_frame + 1) % len(self.gif_frames)

    def display_pil_image(self, pil_img):
        # 应用旋转
        pil_img = pil_img.rotate(self.rotation_angle, expand=True)

        # 转换为QPixmap
        qimage = QImage(
            pil_img.tobytes(),
            pil_img.width,
            pil_img.height,
            pil_img.width * 4,
            QImage.Format.Format_RGBA8888
        )
        self.original_pixmap = QPixmap.fromImage(qimage)
        self.fit_image_to_window()

    def fit_image_to_window(self):
        if self.original_pixmap:
            # 获取窗口和图片的尺寸
            window_size = self.image_label.size()
            pixmap_size = self.original_pixmap.size()

            # 计算缩放比例
            scale = min(
                window_size.width() / pixmap_size.width(),
                window_size.height() / pixmap_size.height()
            )

            # 缩放图片
            scaled_pixmap = self.original_pixmap.scaled(
                pixmap_size * scale,
                Qt.AspectRatioMode.KeepAspectRatio,
                Qt.TransformationMode.SmoothTransformation
            )
            self.image_label.setPixmap(scaled_pixmap)

    def resizeEvent(self, event: QResizeEvent):
        # 窗口大小变化时重新调整图片
        self.fit_image_to_window()
        super().resizeEvent(event)

    def move_image(self, src, dest):
        if src:
            self.next_image()
            self.image_files.remove(src)
            self.image_cache.remove(src)
            shutil.move(src, dest)
            # 检查是否还有图片
            if not self.image_files:
                self.image_label.clear()
                self.status_bar.clearMessage()

    def prev_image(self):
        if not self.image_files:
            return
        if self.current_image_index == 0:
            self.current_image_index = len(self.image_files) - 1
        else:
            self.current_image_index -= 1
        self.load_image(self.image_files[self.current_image_index])
        if len(self.image_files) > self.cache_radius * 2 + 1:
            self.image_cache.remove(self.image_files[self.get_right_edge()])
            self.load_image_to_cache(self.image_files[self.get_left_edge()])

    def next_image(self):
        if len(self.image_files) <= 0:
            return
        if self.current_image_index == len(self.image_files) - 1:
            self.current_image_index = 0  # 循环到第一张图片
        else:
            self.current_image_index += 1
        self.load_image(self.image_files[self.current_image_index])
        if len(self.image_files) > self.cache_radius * 2 + 1:
            self.image_cache.remove(self.image_files[self.get_left_edge()])
            self.load_image_to_cache(self.image_files[self.get_right_edge()])

    def rotate_image(self, angle):
        self.rotation_angle += angle
        self.rotation_angle %= 360
        self.reload_current_image()

    def reload_current_image(self):
        path = self.get_current_image_path()
        if path:
            self.load_image(path)

    def toggle_fullscreen(self):
        if self.is_fullscreen:
            self.showNormal()
            self.is_fullscreen = False
        else:
            self.showFullScreen()
            self.is_fullscreen = True

    def delete_image(self):
        path_to_delete = self.get_current_image_path()
        if path_to_delete:
            # 切换到下一张图片
            self.next_image()
            # 删除文件
            try:
                os.remove(path_to_delete)
            except Exception as e:
                QMessageBox.critical(self, "错误", f"删除失败: {str(e)}")
                return
            # 从列表和缓存中移除
            self.image_files.remove(path_to_delete)
            self.image_cache.remove(path_to_delete)
            # 检查是否还有图片
            if not self.image_files:
                self.image_label.clear()
                self.status_bar.clearMessage()

    def keyPressEvent(self, event: QKeyEvent):
        # 快捷键处理
        key = event.key()
        modifier = event.modifiers()
        shortcut = self.config["shortcut"]
        for obj in shortcut:
            if getattr(Qt, obj["key"]) == key and obj.get("modifier", Qt.KeyboardModifier.NoModifier) == modifier:
                if obj.get("command") == "prev_image":
                    self.prev_image()
                elif obj.get("command") == "next_image":
                    self.next_image()
                elif obj.get("command") == "rotate_right":
                    self.rotate_image(90)  # 顺时针旋转
                elif obj.get("command") == "rotate_left":
                    self.rotate_image(-90)  # 逆时针旋转
                elif obj.get("command") == "enter_fullscreen":
                    self.toggle_fullscreen()
                elif obj.get("command") == "exit_fullscreen":
                    if self.is_fullscreen:
                        self.toggle_fullscreen()
                elif obj.get("command") == "move":
                    self.move_image(self.get_current_image_path(), obj["dir"])
                else:
                    super().keyPressEvent(event)


if __name__ == "__main__":
    app = QApplication(sys.argv)
    viewer = ImageViewer()
    viewer.show()
    sys.exit(app.exec())
