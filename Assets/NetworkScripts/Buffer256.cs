using System;

namespace NetworkScripts{
  public class Buffer256<T>{
    private T[] _buffer = new T[256];
    private int _bufferCount = 0;

    public int Count  => _bufferCount;
    public int Add(T item) {
      _buffer[(byte)_bufferCount] = item;
      return _bufferCount++;
    }

    public T Get(int index) {
      return _buffer[(byte)index];
    }

    public void EditTail(int size, Func<T, T> editFunction) {
      int offset = _bufferCount - size;
      for (int i = 0; i < size; i++) {
        _buffer[(byte)(i + offset)] = editFunction(_buffer[(byte)(i + offset)]);
      }
    }
    
    public T GetLast() {
      return _buffer[(byte)(_bufferCount - 1)];
    }

    public void Clear() {
      _buffer = new T[256];
      _bufferCount = 0;
    }

    public T[] GetTail(int size) {
      var result = new T[size];
      int offset = _bufferCount - size;
      for (int i = 0; i < size; i++) {
        result[i] = _buffer[(byte)(i + offset)];
      }

      return result;
    }
  }
}