import sys
from PySide6.QtWidgets import (
    QMainWindow, QApplication, QLabel, QFileDialog,
    QToolBar, QStatusBar, QWidget, QHBoxLayout
)
from PySide6.QtGui import QAction, QPixmap, QImage, QKeyEvent, QResizeEvent
from PySide6.QtCore import Qt, QTimer
from PIL import Image, ImageSequence
import os


class ImageViewer(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("Simple Image Viewer")
        self.setMinimumSize(800, 600)

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

        # 状态栏
        self.status_bar = QStatusBar()
        self.setStatusBar(self.status_bar)

    def open_image(self):
        file, _ = QFileDialog.getOpenFileName(
            self, "Open Image", "",
            "Images (*.png *.jpg *.jpeg *.gif)"
        )
        if file:
            self.current_image_path = file
            directory = os.path.dirname(file)
            # 使路径名格式一致
            base_name = os.path.basename(file)
            file = os.path.join(directory, base_name)
            self.image_files = [
                os.path.join(directory, f) for f in os.listdir(directory)
                if f.lower().endswith(('.png', '.jpg', '.jpeg', '.gif'))
            ]
            self.current_image_index = self.image_files.index(file)
            self.load_image(file)

    def load_image(self, path):
        # 停止GIF动画
        self.gif_timer.stop()

        # 读取图片
        with Image.open(path) as img:
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

        # 更新状态栏
        self.status_bar.showMessage(f"{os.path.basename(path)} | {self.image_label.pixmap().size().toTuple()}")

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

    def prev_image(self):
        if self.current_image_index > 0:
            self.current_image_index -= 1
            self.load_image(self.image_files[self.current_image_index])

    def next_image(self):
        if self.current_image_index < len(self.image_files) - 1:
            self.current_image_index += 1
            self.load_image(self.image_files[self.current_image_index])

    def rotate_image(self, angle):
        self.rotation_angle += angle
        self.rotation_angle %= 360
        self.reload_current_image()

    def reload_current_image(self):
        if self.current_image_path:
            self.load_image(self.current_image_path)

    def toggle_fullscreen(self):
        if self.is_fullscreen:
            self.showNormal()
            self.is_fullscreen = False
        else:
            self.showFullScreen()
            self.is_fullscreen = True

    def keyPressEvent(self, event: QKeyEvent):
        # 快捷键处理
        key = event.key()
        if key == Qt.Key.Key_Left:
            self.prev_image()
        elif key == Qt.Key.Key_Right:
            self.next_image()
        elif key == Qt.Key.Key_R:
            self.rotate_image(90)  # 顺时针旋转
        elif key == Qt.Key.Key_L:
            self.rotate_image(-90)  # 逆时针旋转
        elif key == Qt.Key.Key_F:
            self.toggle_fullscreen()
        elif key == Qt.Key.Key_Escape:
            if self.is_fullscreen:
                self.toggle_fullscreen()
        else:
            super().keyPressEvent(event)


if __name__ == "__main__":
    app = QApplication(sys.argv)
    viewer = ImageViewer()
    viewer.show()
    sys.exit(app.exec())
