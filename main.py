import sys, shutil, os, json
from PIL import Image, ImageSequence
from PySide6.QtCore import Qt, QTimer
from PySide6.QtGui import (
    QAction, QPixmap, QImage,
    QKeyEvent, QResizeEvent, QIcon
)
from PySide6.QtWidgets import (
    QMainWindow, QApplication, QLabel, QFileDialog,
    QToolBar, QStatusBar, QWidget, QHBoxLayout, QMessageBox
)
from natsort import natsorted
from theme import get_theme
from ArgsParser import parse_arguments, show_help


# 定义深夜风格的颜色主题

class ImageViewer(QMainWindow):
    def __init__(self, args):
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

        # GIF支持
        self.gif_frames = []
        self.current_gif_frame = 0
        self.gif_timer = QTimer(self)
        self.gif_timer.timeout.connect(self.next_gif_frame)

        # 创建界面
        self.create_ui()

        if args:
            if args.directory:
                files = natsorted(os.listdir(args.directory[0]))
                if args.index:
                    self.open_image(os.path.join(args.directory[0], files[min(args.index[0] - 1, len(files) - 1)]))
                else:
                    self.open_image(os.path.join(args.directory[0], files[0]))
            if args.file:
                self.open_image(args.file)

    def get_current_image_path(self):
        if self.image_files:
            return self.image_files[self.current_image_index]

    def load_config(self):
        with open(get_absolute_path("config.json"), 'r') as f:
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
        dark_palette, qss = get_theme()
        QApplication.setPalette(dark_palette)

        # 设置全局样式表
        self.setStyleSheet(qss)
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

    def open_image(self, file=None):
        if not file:
            file, _ = QFileDialog.getOpenFileName(
                self,
                "Open Image",
                "Desktop",
                "Image Files (*.png *.jpg *.jpeg *.gif)"
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
            self.load_image(file)

    def load_image(self, path):
        # 停止GIF动画
        self.gif_timer.stop()
        img = Image.open(path)

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
            os.makedirs(dest, exist_ok=True)
            shutil.move(src, dest)
            # 检查是否还有图片
            if not self.image_files:
                self.image_label.clear()
                self.status_bar.clearMessage()

    def prev_image(self):
        self.rotation_angle = 0
        if not self.image_files:
            return
        if self.current_image_index == 0:
            self.current_image_index = len(self.image_files) - 1
        else:
            self.current_image_index -= 1
        self.load_image(self.image_files[self.current_image_index])

    def next_image(self):
        self.rotation_angle = 0
        if not self.image_files:
            return
        if self.current_image_index == len(self.image_files) - 1:
            self.current_image_index = 0  # 循环到第一张图片
        else:
            self.current_image_index += 1
        self.load_image(self.image_files[self.current_image_index])

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
            # 检查是否还有图片
            if not self.image_files:
                self.image_label.clear()
                self.status_bar.clearMessage()

    def keyPressEvent(self, event: QKeyEvent):
        # 快捷键处理
        key = event.key()
        modifier = event.modifiers()
        shortcut = self.config["shortcut"]
        match = False
        for obj in shortcut:
            modifier_key = obj.get("modifier")
            current_modifier = Qt.KeyboardModifier.NoModifier
            if modifier_key is not None:
                current_modifier = getattr(Qt.KeyboardModifier, modifier_key)
            if getattr(Qt.Key, obj["key"]) == key and current_modifier == modifier:
                match = True
                if obj.get("command") == "prev_image":
                    self.prev_image()
                elif obj.get("command") == "next_image":
                    self.next_image()
                elif obj.get("command") == "rotate_right":
                    self.rotate_image(90)  # 顺时针旋转
                elif obj.get("command") == "rotate_left":
                    self.rotate_image(-90)  # 逆时针旋转
                elif obj.get("command") == "toggle_fullscreen":
                    self.toggle_fullscreen()
                elif obj.get("command") == "exit_app":
                    self.close()
                elif obj.get("command") == "move":
                    self.move_image(self.get_current_image_path(), obj["dir"])
                elif obj.get("command") == "delete_image":
                    self.delete_image()
                else:
                    super().keyPressEvent(event)
            if match:
                break


def get_absolute_path(relative_path) -> str:
    return str(os.path.join(os.path.abspath("."), relative_path))


def main():
    args = parse_arguments(sys.argv[1:])
    if args and args.help:
        show_help()
        sys.exit(0)
    app = QApplication(sys.argv)
    app.setWindowIcon(QIcon(get_absolute_path('icon.ico')))
    viewer = ImageViewer(args)
    viewer.show()
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
