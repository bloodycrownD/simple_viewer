import argparse
from typing import List


def parse_arguments(argv: List[str] = None):
    """解析命令行参数"""
    if not argv:
        return None
    parser = argparse.ArgumentParser(
        description="Simple Image Viewer",
        add_help=False  # 使用自定义帮助输出
    )

    parser.add_argument(
        'file',
        type=str,
        help='输入有效文件路径',
        nargs='?'
    )
    parser.add_argument(
        "-i", "--index",
        nargs=1,
        type=int,
        metavar="INDEX",
        help="直接打开指定图片文件"
    )
    parser.add_argument(
        "-d", "--directory",
        nargs=1,
        metavar="DIR",
        help="打开指定目录并显示第一张图片"
    )

    parser.add_argument(
        "-h", "--help",
        action="store_true",
        help="显示帮助信息"
    )

    return parser.parse_args(argv)


def show_help():
    """显示增强的帮助信息"""
    help_text = """\
图片查看器 v1.0 使用说明

基本用法:
  viewer [文件路径]

选项:
  -d, --directory DIR   打开指定图片目录
  -i, --index     INDEX 指定目录中文件的位置
  -h, --help            显示本帮助信息

示例:
  viewer abc.png
  viewer -d /path/pics -i 10 打开pics目录中第十个图片
  viewer -h\
"""
    print(help_text)
