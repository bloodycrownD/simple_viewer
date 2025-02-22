from PySide6.QtCore import Qt
from PySide6.QtGui import QPalette, QColor


def get_theme():
    dark_palette = QPalette()
    dark_palette.setColor(QPalette.ColorRole.Window, QColor(43, 43, 43))  # 背景色
    dark_palette.setColor(QPalette.ColorRole.WindowText, QColor(169, 183, 198))  # 文本色
    dark_palette.setColor(QPalette.ColorRole.Base, QColor(43, 43, 43))  # 输入框背景色
    dark_palette.setColor(QPalette.ColorRole.AlternateBase, QColor(60, 63, 65))  # 交替背景色
    dark_palette.setColor(QPalette.ColorRole.ToolTipBase, QColor(43, 43, 43))  # 工具提示背景色
    dark_palette.setColor(QPalette.ColorRole.ToolTipText, QColor(169, 183, 198))  # 工具提示文本色
    dark_palette.setColor(QPalette.ColorRole.Text, QColor(169, 183, 198))  # 文本色
    dark_palette.setColor(QPalette.ColorRole.Button, QColor(60, 63, 65))  # 按钮背景色
    dark_palette.setColor(QPalette.ColorRole.ButtonText, QColor(169, 183, 198))  # 按钮文本色
    dark_palette.setColor(QPalette.ColorRole.BrightText, QColor(255, 0, 0))  # 高亮文本色
    dark_palette.setColor(QPalette.ColorRole.Highlight, QColor(64, 128, 214))  # 选中项背景色
    dark_palette.setColor(QPalette.ColorRole.HighlightedText, QColor(255, 255, 255))  # 选中项文本色
    qss = """QMainWindow {
                background-color: #2B2B2B;
            }
            QToolBar {
                background-color: #3C3F41;
                border: none;
                padding: 5px;
            }
            QToolButton {
                background-color: #3C3F41;
                border: 1px solid #555;
                border-radius: 4px;
                padding: 5px;
                color: #A9B7C6;
            }
            QToolButton:hover {
                background-color: #4C5052;
            }
            QToolButton:pressed {
                background-color: #5E6264;
            }
            QStatusBar {
                background-color: #3C3F41;
                color: #A9B7C6;
                border-top: 1px solid #555;
            }
            QLabel {
                border: 2px solid #555;
                border-radius: 4px;
                background-color: #2B2B2B;
            }
            QMessageBox {
                background-color: #3C3F41;
                color: #A9B7C6;
            }
            QMessageBox QLabel {
                color: #A9B7C6;
            }
            QMessageBox QPushButton {
                background-color: #4C5052;
                color: #A9B7C6;
                border: 1px solid #555;
                border-radius: 4px;
                padding: 5px;
            }
            QMessageBox QPushButton:hover {
                background-color: #5E6264;
            }
"""
    return dark_palette, qss

