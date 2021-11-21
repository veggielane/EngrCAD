#pragma once

using namespace System;

namespace EngrCADOCWrapper {
	public ref class NativeWrapper
	{
	public:
		NativeWrapper(void* shape) : m_Impl(shape) {
		}

		~NativeWrapper() {
			this->!NativeWrapper();
		}
	protected:
		!NativeWrapper() {
			delete m_Impl;
		}

	public:
		void* GetPointer() { return m_Impl; }

		static NativeWrapper^ Sphere(float radius);

		NativeWrapper^ Translate(float x, float y, float z);

		void SaveSTP(String^ path);

	private:
		void* m_Impl;
	};

}
